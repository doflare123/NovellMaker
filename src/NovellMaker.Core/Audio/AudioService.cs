using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Audio;

public enum AudioEventKind { Scene, Line, Continue, Jump, Restore, Stop }
public sealed record AudioEvent(AudioEventKind Kind, Guid SceneId, Guid? LineId = null);
public interface IAudioSource : IDisposable
{
    float Volume { set; }
    bool Finished { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    void Update();
    void Pause(bool paused);
}
public interface IAudioBackend
{
    IAudioSource Open(string path, bool loop);
}

/// <summary>Host-independent mixer and playback policy, used by preview and packaged runtime.</summary>
public sealed class AudioService(NovelProject project, string root, string language, IAudioBackend backend, TimeProvider? timeProvider = null) : IDisposable
{
    private sealed class Playback(AudioGroup group, IAudioSource source, AudioOwner? owner, Guid id, float volume)
    {
        internal readonly AudioGroup Group = group;
        internal readonly IAudioSource Source = source;
        internal readonly AudioOwner? Owner = owner;
        internal readonly Guid Id = id;
        internal float Volume = volume;
        internal bool OwnerPaused;
        internal bool VoicePaused;
        internal float Envelope = 1;
        internal float FadeStart = 1, FadeTarget = 1;
        internal double FadeElapsed, FadeDuration;
        internal bool Stopping;
        internal TimeSpan Position, Duration;
        internal bool Loop;
        internal long LastEnvelopeUpdate;
    }
    private readonly List<Playback> sources = [];
    private readonly Dictionary<AudioGroup, AudioBus> mixer = project.Audio.Mixer.ToDictionary();
    private readonly List<string> diagnostics = [];
    private bool paused;
    private bool disposed;
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long lastUpdate = (timeProvider ?? TimeProvider.System).GetTimestamp();
    private float duckGain = 1, duckStart = 1, duckTarget = 1;
    private double duckElapsed;
    private AudioMixSettings Mix => project.Audio.Playback;
    private bool IsPaused(Playback s) => s.Owner is null ? paused || s.VoicePaused : s.OwnerPaused;
    public VoicePlayback Voice => sources.LastOrDefault(s => s.Owner is null && s.Group == AudioGroup.Voice && !s.Stopping) is { } voice
        ? new(voice.Position, voice.Duration, true, IsPaused(voice)) : VoicePlayback.None;
    public void PauseVoice(bool value)
    {
        foreach (var s in sources.Where(s => s.Owner is null && s.Group == AudioGroup.Voice).ToArray())
        {
            if (s.VoicePaused != value) s.LastEnvelopeUpdate = clock.GetTimestamp();
            s.VoicePaused = value; Try(s, () => s.Source.Pause(IsPaused(s)));
        }
    }
    private readonly AudioOwner historyOwner = new(Guid.NewGuid(), 0, Guid.NewGuid(), Guid.NewGuid());
    public bool IsVoicePlaying => sources.Any(s => s.Owner is null && s.Group == AudioGroup.Voice);
    public bool CanReplayVoice(Guid line, string voiceLanguage) => project.Audio.Voices.TryGetValue(line, out var voices) && voices.ContainsKey(voiceLanguage);
    /// <summary>Independent history channel: no line effects, scenario commands or scene music restart.</summary>
    public void ReplayVoice(Guid line, string voiceLanguage)
    {
        StopReplayVoice();
        if (project.Audio.Voices.TryGetValue(line, out var voices) && voices.TryGetValue(voiceLanguage, out var asset))
        {
            Play(asset, AudioGroup.Voice, false, historyOwner, Guid.NewGuid());
            PauseOwner(historyOwner, false);
        }
    }
    public void PauseReplayVoice(bool value) => PauseOwner(historyOwner, value);
    public void StopReplayVoice() => StopOwner(historyOwner);
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
        if (paused != value)
            foreach (var s in sources.Where(s => s.Owner is null)) s.LastEnvelopeUpdate = clock.GetTimestamp();
        paused = value;
        foreach (var s in sources.ToArray()) Try(s, () => s.Source.Pause(IsPaused(s)));
    }
    public void PauseOwner(AudioOwner owner, bool value)
    {
        foreach (var s in sources.Where(s => s.Owner == owner).ToArray())
        {
            if (s.OwnerPaused != value) s.LastEnvelopeUpdate = clock.GetTimestamp();
            s.OwnerPaused = value; Try(s, () => s.Source.Pause(value));
        }
    }
    public void Handle(AudioEvent cue)
    {
        switch (cue.Kind)
        {
            case AudioEventKind.Scene:
                ChangeScene(cue.SceneId);
                break;
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
    private void ChangeScene(Guid sceneId)
    {
        var previous = sources.Where(s => s.Owner is null && s.Group == AudioGroup.Sound).ToArray();
        foreach (var s in sources.Except(previous).ToArray()) End(s, "stopped");
        var hasNext = project.Audio.SceneSounds.TryGetValue(sceneId, out var sound);
        var duration = hasNext && previous.Length > 0 ? Mix.CrossfadeSeconds : Mix.FadeOutSeconds;
        // Open the replacement before retiring music: a missing file must not cut working music.
        if (hasNext && !Play(sound, AudioGroup.Sound, true, fadeIn: previous.Length > 0 && Mix.CrossfadeSeconds > 0 ? Mix.CrossfadeSeconds : Mix.FadeInSeconds)) return;
        foreach (var s in previous) Fade(s, 0, duration, stopping: true);
    }
    private bool Play(Guid id, AudioGroup group, bool loop, AudioOwner? owner = null, Guid sourceId = default, float volume = 1, double? fadeIn = null)
    {
        IAudioSource? source = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sources.Count >= 32) throw new InvalidOperationException("Достигнут лимит 32 одновременных звуков.");
            var asset = project.Audio.Assets.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"Нет звукового ресурса {id}.");
            source = backend.Open(AudioAssetPath.Resolve(root, asset.RelativePath), loop);
            var playback = new Playback(group, source, owner, sourceId, volume)
                { OwnerPaused = owner is not null, Duration = source.Duration, Loop = loop, LastEnvelopeUpdate = clock.GetTimestamp() };
            var fade = fadeIn ?? Mix.FadeInSeconds;
            if (fade > 0) { playback.Envelope = 0; Fade(playback, 1, fade); }
            source.Volume = Gain(playback);
            // A Lua source starts held until the host supplies its owner's current activity.
            source.Pause(owner is not null || paused);
            sources.Add(playback);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (source is not null) DisposeSource(source);
            Diagnose($"Звук {id}: {e.Message}");
            if (owner is not null) PlaybackEnded?.Invoke(owner, new(sourceId, "failed", Clip(e.Message)));
            return false;
        }
    }
    public void Update()
    {
        var now = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(lastUpdate, now).TotalSeconds;
        lastUpdate = now;
        foreach (var s in sources.ToArray())
        {
            try
            {
                var sourceElapsed = clock.GetElapsedTime(s.LastEnvelopeUpdate, now).TotalSeconds;
                s.LastEnvelopeUpdate = now;
                if (IsPaused(s)) continue;
                s.Source.Update();
                s.Position = s.Source.Position;
                if (!s.Source.Finished)
                {
                    s.FadeElapsed = Math.Min(s.FadeDuration, s.FadeElapsed + sourceElapsed);
                    if (s.FadeDuration > 0) s.Envelope = float.Lerp(s.FadeStart, s.FadeTarget, (float)(s.FadeElapsed / s.FadeDuration));
                    if (s.Stopping && s.FadeElapsed >= s.FadeDuration) { End(s, "stopped"); continue; }
                    continue;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            { End(s, "failed", e.Message); continue; }
            End(s, "completed");
        }
        UpdateDucking(elapsed);
        ApplyVolumes();
    }
    private float Gain(AudioGroup group) => (Bus(AudioGroup.Master).Muted || Bus(group).Muted) ? 0 : Bus(AudioGroup.Master).Volume * Bus(group).Volume;
    private float Gain(Playback s) => Gain(s.Group) * s.Volume * s.Envelope * (s.Group == AudioGroup.Sound ? duckGain : 1) *
        (!s.Stopping && Mix.FadeOutSeconds > 0 && s.Duration > TimeSpan.Zero && !s.Loop
            ? (float)Math.Clamp((s.Duration - s.Position).TotalSeconds / Mix.FadeOutSeconds, 0, 1) : 1);
    private void ApplyVolumes() { foreach (var s in sources.ToArray()) Try(s, () => s.Source.Volume = Gain(s)); }
    private void Fade(Playback s, float target, double duration, bool stopping = false)
    {
        s.FadeStart = s.Envelope; s.FadeTarget = target; s.FadeElapsed = 0; s.FadeDuration = duration; s.Stopping = stopping;
        s.LastEnvelopeUpdate = clock.GetTimestamp();
        if (duration == 0) { s.Envelope = target; if (stopping) End(s, "stopped"); }
    }
    private void UpdateDucking(double elapsed)
    {
        // Paused/muted/failed voices, including Lua and history channels, cannot hold music down.
        var target = Mix.DuckingEnabled && Gain(AudioGroup.Voice) > 0 && sources.Any(s => s.Group == AudioGroup.Voice &&
            !IsPaused(s) && s.Volume > 0 && !s.Stopping) ? Mix.DuckingVolume : 1;
        if (target != duckTarget) { duckStart = duckGain; duckTarget = target; duckElapsed = 0; }
        var duration = target < duckStart ? Mix.DuckingAttackSeconds : Mix.DuckingReleaseSeconds;
        duckElapsed += elapsed;
        duckGain = duration <= 0 ? target : float.Lerp(duckStart, target, (float)Math.Clamp(duckElapsed / duration, 0, 1));
    }
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
            else { source.Volume = (float)command.Volume!.Value; Try(source, () => source.Source.Volume = Gain(source)); }
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
        if (sources.Count == 0) { duckGain = duckStart = duckTarget = 1; duckElapsed = 0; lastUpdate = clock.GetTimestamp(); }
    }
    public void Dispose() { if (disposed) return; disposed = true; Stop(); }
}
