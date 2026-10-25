namespace NovellMaker.Runtime;

internal static class RuntimeStorage
{
    internal static string LuaJournalPath(Guid projectId, bool packaged) => Path.Combine(Path.GetDirectoryName(ProfilePath(projectId, packaged))!, "lua-diagnostics.log");
    internal static string ProfilePath(Guid projectId, bool packaged) => packaged
        ? Path.Combine(GameDirectory(projectId), "profile.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Saves", projectId.ToString(), "profile.json");
    internal static string SlotsDirectory(Guid projectId, bool packaged) => packaged
        ? Path.Combine(GameDirectory(projectId), "slots")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Saves", projectId.ToString(), "slots");
    internal static string GameDirectory(Guid projectId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Games", projectId.ToString());

    internal static string SavePath(Guid projectId, bool packaged) => packaged
        ? Path.Combine(GameDirectory(projectId), "quicksave.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Saves", projectId + ".json");

    internal static string WriteStartupError(Guid? projectId, Exception error)
    {
        var directory = projectId is { } id ? GameDirectory(id) : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "Logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "startup-error.log");
        File.WriteAllText(path, $"{DateTimeOffset.Now:O}\n{error}");
        return path;
    }
}
