using System.Text;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Projects;
using System.Collections.Immutable;

namespace NovellMaker.Testing;

internal static class AudioFixture
{
    public static byte[] Wave(int samples = 24000)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(24000); writer.Write(48000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(i * 2 * Math.PI * 440 / 24000)));
        return output.ToArray();
    }
    public static NovelProject Attach(NovelProject project, string root)
    {
        var id = Guid.NewGuid(); var relative = $"Assets/Audio/{id:N}.wav";
        Directory.CreateDirectory(Path.Combine(root, "Assets", "Audio"));
        File.WriteAllBytes(Path.Combine(root, relative), Wave());
        var first = project.Scenes[0]; var line = first.Lines[0];
        return project with { Audio = new AudioProject {
            Assets = [new AudioAsset(id, "Проверка.wav", relative)],
            SceneSounds = ImmutableDictionary<Guid, Guid>.Empty.Add(first.Id, id),
            LineEffects = ImmutableDictionary<Guid, Guid>.Empty.Add(line.Id, id),
            Voices = ImmutableDictionary<Guid, ImmutableDictionary<string, Guid>>.Empty.Add(line.Id, ImmutableDictionary<string, Guid>.Empty.Add("ru", id))
        } };
    }
}
