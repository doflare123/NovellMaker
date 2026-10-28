using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Audio;

public enum AudioEventKind { Scene, Line, Continue, Jump, Restore, Stop }
public sealed record AudioEvent(AudioEventKind Kind, Guid SceneId, Guid? LineId = null);
public interface IAudioSource : IDisposable
{
    float Volume { set; }
    bool Finished { get; }
    void Update();
    void Pause(bool paused);
}
public interface IAudioBackend
{
    IAudioSource Open(string path, bool loop);
}

/// <summary>Host-independent mixer and playback policy, used by preview and packaged runtime.</summary>
public sealed class AudioService(NovelProject project, string root, string language, IAudioBackend backend) : IDisposable
{
    private sealed class Playback(AudioGroup group, IAudioSource source, AudioOwner? owner, Guid id, float volume)
    {
        internal readonly AudioGroup Group = group;
        internal readonly IAudioSource Source = source;
        internal readonly AudioOwner? Owner = owner;
        internal readonly Guid Id = id;
        internal float Volume = volume;
        internal bool OwnerPaused;
    }
    private readonly List<Playback> sources = [];
    private readonly Dictionary<AudioGroup, AudioBus> mixer = project.Audio.Mixer.ToDictionary();
    private readonly List<string> diagnostics = [];
    private bool paused;
    private bool disposed;
    public event Action<AudioOwner, ScriptAudioResult>? PlaybackEnded;
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public int ActiveSources => sources.Count;
    public AudioBus Bus(AudioGroup group) => mixer.GetValueOrDefault(group) ?? new();
    public void SetBus(AudioGroup group, AudioBus bus)
    {
        if (!Enum.IsDefined(group) || !float.IsFinite(bus.Volume) || bus.Volume is < 0 or > 1) throw new ArgumentException("Громкость: 0–1.");
        mixer[group] = bus;
        ApplyVolumes();
    }
    public void Pause(bool value)
    {
        paused = value;
        foreach (var s in sources.ToArray()) Try(s, () => s.Source.Pause(s.Owner is null ? value : s.OwnerPaused));
    }
    public void PauseOwner(AudioOwner owner, bool value)
    {
        foreach (var s in sources.Where(s => s.Owner == owner).ToArray())
        { s.OwnerPaused = value; Try(s, () => s.Source.Pause(value)); }
    }
    public void Handle(AudioEvent cue)
    {
        switch (cue.Kind)
        {
            case AudioEventKind.Scene:
            case AudioEventKind.Restore:
                Stop();
                if (project.Audio.SceneSounds.TryGetValue(cue.SceneId, out var sound)) Play(sound, AudioGroup.Sound, true);
                break;
            case AudioEventKind.Line:
                StopBindings(AudioGroup.Voice);
                if (cue.LineId is { } line)
                {
                    if (project.Audio.Voices.TryGetValue(line, out var voices) && voices.TryGetValue(language, out var voice)) Play(voice, AudioGroup.Voice, false);
                    if (project.Audio.LineEffects.TryGetValue(line, out var effect)) Play(effect, AudioGroup.Effect, false);
                }
                break;
            case AudioEventKind.Continue: StopBindings(AudioGroup.Voice); break;
            case AudioEventKind.Jump: StopBindings(AudioGroup.Voice); StopBindings(AudioGroup.Effect); break;
            case AudioEventKind.Stop: Stop(); break;
        }
    }
    private void Play(Guid id, AudioGroup group, bool loop, AudioOwner? owner = null, Guid sourceId = default, float volume = 1)
    {
        IAudioSource? source = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sources.Count >= 32) throw new InvalidOperationException("Достигнут лимит 32 одновременных звуков.");
            var asset = project.Audio.Assets.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"Нет звукового ресурса {id}.");
            source = backend.Open(AudioAssetPath.Resolve(root, asset.RelativePath), loop);
            source.Volume = Gain(group) * volume;
            // A Lua source starts held until the host supplies its owner's current activity.
            source.Pause(owner is not null || paused);
            sources.Add(new(group, source, owner, sourceId, volume) { OwnerPaused = owner is not null });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (source is not null) DisposeSource(source);
            Diagnose($"Звук {id}: {e.Message}");
            if (owner is not null) PlaybackEnded?.Invoke(owner, new(sourceId, "failed", Clip(e.Message)));
        }
    }
    public void Update()
    {
        foreach (var s in sources.ToArray())
        {
            try
            {
                if (s.Owner is null ? paused : s.OwnerPaused) continue;
                s.Source.Update(); if (!s.Source.Finished) continue;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            { End(s, "failed", e.Message); continue; }
            End(s, "completed");
        }
    }
    private float Gain(AudioGroup group) => (Bus(AudioGroup.Master).Muted || Bus(group).Muted) ? 0 : Bus(AudioGroup.Master).Volume * Bus(group).Volume;
    private void ApplyVolumes() { foreach (var s in sources.ToArray()) Try(s, () => s.Source.Volume = Gain(s.Group) * s.Volume); }
    private void Diagnose(string message) { if (!diagnostics.Contains(message) && diagnostics.Count < 32) diagnostics.Add(message); }
    private static string Clip(string message) => message.Length <= 1024 ? message : message[..1024];
    private string? DisposeSource(IAudioSource source)
    {
        try { source.Dispose(); return null; }
        catch (Exception e) when (e is IOException or InvalidOperationException) { Diagnose("Звук: " + e.Message); return e.Message; }
    }
    private void End(Playback source, string status, string message = "", bool notify = true)
    {
        if (!sources.Remove(source)) return;
        if (DisposeSource(source.Source) is { } error) { status = "failed"; message = error; }
        if (status == "failed") Diagnose("Звук: " + message);
        if (notify && source.Owner is { } owner) PlaybackEnded?.Invoke(owner, new(source.Id, status, Clip(message)));
    }
    private void Try(Playback source, Action action)
    {
        try { action(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { End(source, "failed", e.Message); }
    }
    /// <summary>Only committed, fully validated Lua batches call this on the host thread.</summary>
    public void Execute(AudioOwner owner, ScriptAudioCommand command)
    {
        command.Validate();
        if (command.Kind == "play")
        {
            if (sources.Any(s => s.Owner == owner && s.Id == command.Id)) throw new ArgumentException("Повторный ID источника.");
            Play(command.AssetId!.Value, command.Group!.Value, command.Loop!.Value, owner, command.Id, (float)command.Volume!.Value);
        }
        else if (sources.FirstOrDefault(s => s.Owner == owner && s.Id == command.Id) is { } source)
        {
            if (command.Kind == "stop") End(source, "stopped");
            else { source.Volume = (float)command.Volume!.Value; Try(source, () => source.Source.Volume = Gain(source.Group) * source.Volume); }
        }
        // A source can finish between dispatch and commit. Its reserved terminal result remains authoritative.
    }
    public void StopOwner(AudioOwner owner)
    { foreach (var s in sources.Where(s => s.Owner == owner).ToArray()) End(s, "stopped", notify: false); }
    private void StopBindings(AudioGroup group)
    { foreach (var s in sources.Where(s => s.Owner is null && s.Group == group).ToArray()) End(s, "stopped"); }
    public void Stop(AudioGroup? group = null)
    {
        foreach (var s in sources.Where(s => group is null || s.Group == group).ToArray()) End(s, "stopped");
    }
    public void Dispose() { if (disposed) return; disposed = true; Stop(); }
}
