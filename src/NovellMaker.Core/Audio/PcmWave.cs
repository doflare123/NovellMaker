using System.Text;

namespace NovellMaker.Core.Audio;

/// <summary>Bounded RIFF inspection; audio data remains on disk. No decoder or host dependency.</summary>
public sealed record PcmWave(int SampleRate, int Channels, long Offset, int Length)
{
    public static PcmWave Inspect(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        string Tag() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (stream.Length < 12 || Tag() != "RIFF") throw new InvalidDataException("Ожидается RIFF PCM WAV.");
        var end = (long)reader.ReadUInt32() + 8;
        if (end != stream.Length || Tag() != "WAVE") throw new InvalidDataException("Повреждён заголовок WAV.");
        int rate = 0, channels = 0; long offset = 0; int length = 0;
        var chunks = 0;
        while (stream.Position + 8 <= end)
        {
            if (++chunks > 1024) throw new InvalidDataException("Слишком много блоков WAV.");
            var tag = Tag(); var size = reader.ReadUInt32(); var start = stream.Position;
            if (size > int.MaxValue || start + size + (size & 1) > end) throw new InvalidDataException("Обрезанный блок WAV.");
            if (tag == "fmt ")
            {
                if (rate != 0 || size < 16 || reader.ReadUInt16() != 1) throw new InvalidDataException("Нужен WAV PCM 16 bit (без сжатия).");
                channels = reader.ReadUInt16(); rate = reader.ReadInt32(); var bytes = reader.ReadInt32();
                var align = reader.ReadUInt16(); var bits = reader.ReadUInt16();
                if (channels is < 1 or > 2 || rate is < 8000 or > 48000 || bits != 16 || align != channels * 2 || bytes != rate * align)
                    throw new InvalidDataException("WAV: PCM 16 bit, mono/stereo, 8000–48000 Гц.");
            }
            else if (tag == "data")
            {
                if (offset != 0) throw new InvalidDataException("Повторный data-блок WAV.");
                offset = start; length = (int)size;
            }
            stream.Position = start + size + (size & 1);
        }
        if (stream.Position != end || rate == 0 || offset == 0 || length <= 0 || length % (channels * 2) != 0)
            throw new InvalidDataException("Нет корректных PCM-данных WAV.");
        return new(rate, channels, offset, length);
    }
}
