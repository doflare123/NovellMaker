using System.Security.Cryptography;

namespace NovellMaker.Editor.Services;

internal static class ProjectFileRevision
{
    internal static async Task<string> ReadAsync(string path, FileShare share = FileShare.Read)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        catch (FileNotFoundException) { return "missing"; }
        catch (DirectoryNotFoundException) { return "missing"; }
    }
}
