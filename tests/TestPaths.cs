namespace NovellMaker;

internal static class TestPaths
{
    // macOS temp paths commonly start with /var -> /private/var. Storage tests
    // need a physical base path so they exercise their own files and links.
    internal static string TempDirectory { get; } = PhysicalDirectory(Path.GetTempPath());

    private static string PhysicalDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Parent is not { } parent) return directory.FullName;
        var physical = Path.Combine(PhysicalDirectory(parent.FullName), directory.Name);
        var info = new DirectoryInfo(physical);
        return (info.Attributes & FileAttributes.ReparsePoint) != 0
            ? info.ResolveLinkTarget(returnFinalTarget: true)!.FullName
            : physical;
    }
}
