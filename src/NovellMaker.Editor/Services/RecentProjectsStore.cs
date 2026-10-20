using System.Text.Json;

namespace NovellMaker.Editor.Services;

public sealed record RecentProject(string Name, string Path);

public sealed class RecentProjectsStore(string? path)
{
    public static string ProfilePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "recent-projects.json");
    public IReadOnlyList<RecentProject> Read()
    {
        try { return path is not null && File.Exists(path) ? (JsonSerializer.Deserialize<RecentProject[]>(File.ReadAllText(path)) ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Path)).Take(10).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    public void Write(IEnumerable<RecentProject> projects)
    {
        if (path is null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(projects.Take(10))); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
