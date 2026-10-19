using NovellMaker.Core.Audio;
using System.Security.Cryptography;

namespace NovellMaker.Editor.Services.Assets;

public static class AudioAssetService
{
    public static async Task<AudioAsset> ImportAsync(string root, string source)
    {
        var id = Guid.NewGuid();
        var relative = $"Assets/Audio/{id:N}.wav";
        var target = AudioAssetPath.Resolve(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        target = AudioAssetPath.Resolve(root, relative);
        var staging = target + ".tmp";
        try
        {
            await CopyFile(source, staging);
            using (var input = File.OpenRead(staging)) _ = PcmWave.Inspect(input);
            File.Move(staging, target, false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new(id, Path.GetFileName(source), relative);
    }
    public static async Task CopyAsync(string source, string target, IReadOnlyList<AudioAsset> assets)
    {
        foreach (var asset in assets)
        {
            var from = AudioAssetPath.Resolve(source, asset.RelativePath);
            var to = AudioAssetPath.Resolve(target, asset.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            to = AudioAssetPath.Resolve(target, asset.RelativePath);
            var staging = to + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await CopyFile(from, staging);
                using (var input = File.OpenRead(staging)) _ = PcmWave.Inspect(input);
                if (File.Exists(to))
                {
                    using var left = File.OpenRead(staging); using var right = File.OpenRead(to);
                    if (left.Length != right.Length || !SHA256.HashData(left).SequenceEqual(SHA256.HashData(right)))
                        throw new IOException($"Звук «{asset.Name}» уже существует и отличается.");
                }
                else File.Move(staging, to, false);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
    }
    private static async Task CopyFile(string from, string to)
    {
        using var source = File.OpenRead(from);
        if (source.Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("WAV больше 2 ГиБ.");
        using var destination = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination);
    }
}
