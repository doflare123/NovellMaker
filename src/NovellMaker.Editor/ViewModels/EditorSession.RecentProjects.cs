using System.Collections.ObjectModel;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private readonly RecentProjectsStore recentProjectsStore;
    public ObservableCollection<RecentProject> RecentProjects { get; } = [];
    private void RememberProject()
    {
        if (projectPath is null || project is null) return;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var entries = RecentProjects.Where(p => !p.Path.Equals(projectPath, comparison)).Prepend(new RecentProject(project.Name, projectPath)).Take(10).ToArray();
        RecentProjects.Clear(); foreach (var entry in entries) RecentProjects.Add(entry);
        try { recentProjectsStore.Write(entries); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log("Не удалось сохранить список последних проектов: " + error.Message); }
    }
    public Task OpenRecentProjectAsync(string path) => RunAsync(() => OpenProjectCoreAsync(path));
}
