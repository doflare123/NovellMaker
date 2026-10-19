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
    private readonly List<(AudioGroup Group, IAudioSource Source)> sources = [];
    private readonly Dictionary<AudioGroup, AudioBus> mixer = project.Audio.Mixer.ToDictionary();
    private readonly List<string> diagnostics = [];
    private bool paused;
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public int ActiveSources => sources.Count;
    public AudioBus Bus(AudioGroup group) => mixer.GetValueOrDefault(group) ?? new();
    public void SetBus(AudioGroup group, AudioBus bus)
    {
        if (!Enum.IsDefined(group) || !float.IsFinite(bus.Volume) || bus.Volume is < 0 or > 1) throw new ArgumentException("Громкость: 0–1.");
        mixer[group] = bus;
        ApplyVolumes();
    }
    public void Pause(bool value) { paused = value; foreach (var s in sources) s.Source.Pause(value); }
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
                Stop(AudioGroup.Voice);
                if (cue.LineId is { } line)
                {
                    if (project.Audio.Voices.TryGetValue(line, out var voices) && voices.TryGetValue(language, out var voice)) Play(voice, AudioGroup.Voice, false);
                    if (project.Audio.LineEffects.TryGetValue(line, out var effect)) Play(effect, AudioGroup.Effect, false);
                }
                break;
            case AudioEventKind.Continue: Stop(AudioGroup.Voice); break;
            case AudioEventKind.Jump: Stop(AudioGroup.Voice); Stop(AudioGroup.Effect); break;
            case AudioEventKind.Stop: Stop(); break;
        }
    }
    private void Play(Guid id, AudioGroup group, bool loop)
    {
        IAudioSource? source = null;
        try
        {
            if (sources.Count >= 32) throw new InvalidOperationException("Достигнут лимит 32 одновременных звуков.");
            var asset = project.Audio.Assets.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"Нет звукового ресурса {id}.");
            source = backend.Open(AudioAssetPath.Resolve(root, asset.RelativePath), loop);
            source.Volume = Gain(group);
            source.Pause(paused);
            sources.Add((group, source));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            source?.Dispose();
            var message = $"Звук {id}: {e.Message}";
            if (!diagnostics.Contains(message) && diagnostics.Count < 32) diagnostics.Add(message);
        }
    }
    public void Update()
    {
        foreach (var s in sources.ToArray())
        {
            try { s.Source.Update(); if (!s.Source.Finished) continue; }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            { if (diagnostics.Count < 32) diagnostics.Add("Звук: " + e.Message); }
            s.Source.Dispose(); sources.Remove(s);
        }
    }
    private float Gain(AudioGroup group) => (Bus(AudioGroup.Master).Muted || Bus(group).Muted) ? 0 : Bus(AudioGroup.Master).Volume * Bus(group).Volume;
    private void ApplyVolumes() { foreach (var s in sources) s.Source.Volume = Gain(s.Group); }
    public void Stop(AudioGroup? group = null)
    {
        foreach (var s in sources.Where(s => group is null || s.Group == group).ToArray()) { s.Source.Dispose(); sources.Remove(s); }
    }
    public void Dispose() => Stop();
}
