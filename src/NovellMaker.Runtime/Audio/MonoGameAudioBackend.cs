using Microsoft.Xna.Framework.Audio;
using NovellMaker.Core.Audio;
using System.Diagnostics;

namespace NovellMaker.Runtime.Audio;

internal sealed class MonoGameAudioBackend : IAudioBackend
{
    public long SubmittedBytes { get; private set; }
    public int OpenedSources { get; private set; }
    public int PausedSources { get; private set; }
    public float LastSourceVolume { get; private set; }
    public int PlaybackPauseCount { get; private set; }
    public double PlaybackPausedSeconds { get; private set; }
    public double LongestPlaybackPauseSeconds { get; private set; }
    public int BufferUnderruns { get; private set; }
    public IAudioSource Open(string path, bool loop)
    {
        try { var source = new WaveSource(path, loop, this); OpenedSources++; return source; }
        catch (Exception e) when (e is NoAudioHardwareException or InstancePlayLimitException)
        { throw new InvalidOperationException("Аудиоустройство недоступно: " + e.Message, e); }
    }

    private sealed class WaveSource : IAudioSource
    {
        private readonly FileStream stream;
        private readonly PcmWave wave;
        private readonly DynamicSoundEffectInstance sound;
        private readonly bool loop;
        private readonly byte[] buffer;
        private int remaining;
        private bool paused;
        private readonly MonoGameAudioBackend metrics;
        private bool started;
        private long? playbackPausedAt;
        public float Volume { set { AudioOperation(() => sound.Volume = value); metrics.LastSourceVolume = value; } }
        public bool Finished => !loop && remaining == 0 && sound.PendingBufferCount == 0;
        public WaveSource(string path, bool loop, MonoGameAudioBackend metrics)
        {
            this.metrics = metrics;
            stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                wave = PcmWave.Inspect(stream);
                this.loop = loop;
                remaining = wave.Length;
                stream.Position = wave.Offset;
                // Three 100 ms buffers per source: memory independent of recording length.
                buffer = new byte[(wave.SampleRate / 10) * wave.Channels * 2];
                sound = new DynamicSoundEffectInstance(wave.SampleRate, (AudioChannels)wave.Channels);
            }
            catch { stream.Dispose(); throw; }
        }
        private static void AudioOperation(Action action)
        {
            try { action(); }
            catch (Exception error) when (error is NoAudioHardwareException or InstancePlayLimitException)
            { throw new InvalidOperationException("Аудиоустройство недоступно: " + error.Message, error); }
        }
        public void Update() => AudioOperation(UpdateCore);
        private void UpdateCore()
        {
            if (paused) return;
            if (started && loop && sound.State == SoundState.Stopped && sound.PendingBufferCount == 0) metrics.BufferUnderruns++;
            while (sound.PendingBufferCount < 3)
            {
                if (remaining == 0)
                {
                    if (!loop) break;
                    stream.Position = wave.Offset; remaining = wave.Length;
                }
                var count = 0;
                do
                {
                    var part = Math.Min(buffer.Length - count, remaining);
                    stream.ReadExactly(buffer.AsSpan(count, part));
                    count += part; remaining -= part;
                    if (remaining == 0 && loop) { stream.Position = wave.Offset; remaining = wave.Length; }
                } while (loop && count < buffer.Length);
                sound.SubmitBuffer(buffer, 0, count);
                metrics.SubmittedBytes += count;
            }
            if (sound.PendingBufferCount > 0 && sound.State == SoundState.Stopped) { sound.Play(); started = true; }
        }
        public void Pause(bool value) => AudioOperation(() => PauseCore(value));
        private void PauseCore(bool value)
        {
            if (paused != value) metrics.PausedSources += value ? 1 : -1;
            paused = value;
            if (value && sound.State == SoundState.Playing)
            { sound.Pause(); metrics.PlaybackPauseCount++; playbackPausedAt = Stopwatch.GetTimestamp(); }
            else if (!value && sound.State == SoundState.Paused) { sound.Resume(); EndPlaybackPause(); }
        }
        private void EndPlaybackPause()
        {
            if (playbackPausedAt is not { } start) return;
            var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds; playbackPausedAt = null;
            metrics.PlaybackPausedSeconds += seconds;
            metrics.LongestPlaybackPauseSeconds = Math.Max(metrics.LongestPlaybackPauseSeconds, seconds);
        }
        public void Dispose()
        {
            if (paused) { metrics.PausedSources--; paused = false; }
            EndPlaybackPause();
            try { AudioOperation(sound.Dispose); } finally { stream.Dispose(); }
        }
    }
}
