using NovellMaker.Core.Audio;

namespace NovellMaker.Editor.Services;

/// <summary>Single streaming take, using the runtime backend. The view owns its UI-thread update timer.</summary>
internal sealed class VoiceAudition(IAudioBackend backend) : IDisposable
{
    private IAudioSource? source;
    public bool IsPlaying => source is not null;
    public void Play(string path, float volume)
    {
        Stop();
        source = backend.Open(path, false);
        try { source.Volume = volume; source.Update(); }
        catch { Stop(); throw; }
    }
    public void Update()
    {
        if (source is null) return;
        try { source.Update(); if (source.Finished) Stop(); }
        catch { Stop(); throw; }
    }
    public void Stop()
    {
        var old = source; source = null; old?.Dispose();
    }
    public void Dispose() => Stop();
}
