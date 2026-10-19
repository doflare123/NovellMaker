using Microsoft.Xna.Framework.Audio;
using NovellMaker.Core.Audio;

namespace NovellMaker.Runtime.Audio;

internal sealed class MonoGameAudioBackend : IAudioBackend
{
    public long SubmittedBytes { get; private set; }
    public int OpenedSources { get; private set; }
    public IAudioSource Open(string path, bool loop)
    {
        try { var source = new WaveSource(path, loop, count => SubmittedBytes += count); OpenedSources++; return source; }
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
        private readonly Action<int> submitted;
        public float Volume { set => sound.Volume = value; }
        public bool Finished => !loop && remaining == 0 && sound.PendingBufferCount == 0;
        public WaveSource(string path, bool loop, Action<int> submitted)
        {
            this.submitted = submitted;
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
        public void Update()
        {
            if (paused) return;
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
                submitted(count);
            }
            if (sound.PendingBufferCount > 0 && sound.State == SoundState.Stopped) sound.Play();
        }
        public void Pause(bool value)
        {
            paused = value;
            if (value && sound.State == SoundState.Playing) sound.Pause();
            else if (!value && sound.State == SoundState.Paused) sound.Resume();
        }
        public void Dispose() { sound.Dispose(); stream.Dispose(); }
    }
}
