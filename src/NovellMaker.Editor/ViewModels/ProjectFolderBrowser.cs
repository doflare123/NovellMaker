using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NovellMaker.Core.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed record FolderTile(ExplorerEntry Entry, ImageAsset? Image, string? ProjectDirectory, int Revision)
{
    public string Name => Image?.Name ?? Entry.Label.TrimEnd('/');
    public string GitStatus => Entry.GitStatus;
    public bool HasGitStatus => GitStatus.Length > 0;
    public string Detail => Entry.Detail + (HasGitStatus ? " — Git: " + GitStatus : "");
    public bool IsFolder => Entry.IsFolder;
    public bool HasImage => Image is not null;
    public bool IsDocument => !IsFolder && !HasImage;
}

public sealed record FolderBreadcrumb(string Name, int Depth);

/// <summary>Navigation for the project browser; folders never change the active document.</summary>
public sealed class ProjectFolderBrowser : INotifyPropertyChanged, IDisposable
{
    private readonly EditorSession session;
    private readonly List<ExplorerEntry> path = [];
    private readonly Stack<string[]> back = [];
    private readonly Stack<string[]> forward = [];
    private string search = "";
    private FolderTile? selectedTile;
    private Guid? projectId;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ExplorerEntry> Roots { get; } = [];
    public ObservableCollection<FolderTile> Tiles { get; } = [];
    public ObservableCollection<FolderBreadcrumb> Breadcrumbs { get; } = [];
    public AsyncCommand BackCommand { get; }
    public AsyncCommand ForwardCommand { get; }
    public AsyncCommand UpCommand { get; }
    public string Search
    {
        get => search;
        set { if (search == value) return; search = value ?? ""; Changed(); RefreshTiles(); }
    }
    public FolderTile? SelectedTile
    {
        get => selectedTile;
        set { var previous = selectedTile?.Entry.ScriptId; selectedTile = value; Changed(); Changed(nameof(SelectionDetail)); if (value?.Entry.ScriptId is { } id && id != previous) session.InspectScript(id); }
    }
    public string SelectionDetail => SelectedTile?.Detail ?? $"Элементов: {Tiles.Count}";
    public bool IsEmpty => Tiles.Count == 0;
    public string EmptyMessage => search.Length > 0 ? "Ничего не найдено в этой папке." : "Папка пуста.";
    public ExplorerEntry? CurrentFolder => path.LastOrDefault();
    public string CurrentPath => string.Join(" / ", path.Select(entry => entry.Label.TrimEnd('/')));

    public ProjectFolderBrowser(EditorSession session)
    {
        this.session = session;
        BackCommand = EditorSession.Command(GoBack, () => back.Count > 0);
        ForwardCommand = EditorSession.Command(GoForward, () => forward.Count > 0);
        UpCommand = EditorSession.Command(() => NavigateToDepth(path.Count - 2), () => path.Count > 1);
        session.Explorer.CollectionChanged += ExplorerChanged;
        Refresh();
    }

    private static string Key(ExplorerEntry entry) => entry.FilePath is { } file ? "file:" + file :
        entry.ScreenId is { } screen ? $"screen:{screen}" :
        entry.ScriptId is { } script ? $"script:{script}" :
        entry.SceneId is { } scene ? $"scene:{scene}:{entry.IsFolder}" :
        entry.CharacterId is { } character ? $"character:{character}:{entry.IsFolder}:{(entry.IsFolder ? "" : entry.Label)}" : entry.Label;
    private string[] Address() => path.Skip(1).Select(Key).ToArray();
    private void ExplorerChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var address = Address();
        if (projectId != session.Project?.Id)
        {
            projectId = session.Project?.Id; back.Clear(); forward.Clear(); address = []; search = "";
            Changed(nameof(Search));
        }
        Roots.Clear();
        if (session.HasProject && session.Explorer.FirstOrDefault() is { } root) Roots.Add(root);
        RestorePath(address);
    }

    private void RestorePath(string[] address)
    {
        path.Clear();
        if (Roots.FirstOrDefault() is { } root)
        {
            path.Add(root);
            foreach (var key in address)
            {
                var folder = path[^1].Items.FirstOrDefault(entry => entry.IsFolder && Key(entry) == key);
                if (folder is null) break;
                path.Add(folder);
            }
        }
        Update();
    }

    public void Navigate(ExplorerEntry folder)
    {
        if (!folder.IsFolder || path.Count == 0) return;
        // Resolve only through the displayed directory tree, avoiding a recursive disk walk.
        var depth = path.FindIndex(entry => ReferenceEquals(entry, folder));
        if (depth >= 0) { NavigateToDepth(depth); return; }
        var parent = path.FindLastIndex(entry => entry.Items.Any(child => ReferenceEquals(child, folder)));
        if (parent < 0)
        {
            // A navigation-tree selection can be deeper than the current directory.
            if (!FindExpandedPath(Roots[0], folder, new List<ExplorerEntry>(), out var found)) return;
            back.Push(Address()); forward.Clear(); path.Clear(); path.AddRange(found);
        }
        else
        {
            back.Push(Address()); forward.Clear(); path.RemoveRange(parent + 1, path.Count - parent - 1); path.Add(folder);
        }
        search = ""; Changed(nameof(Search)); Update();
    }

    private static bool FindExpandedPath(ExplorerEntry node, ExplorerEntry target, List<ExplorerEntry> ancestors, out List<ExplorerEntry> found)
    {
        ancestors.Add(node);
        if (ReferenceEquals(node, target)) { found = ancestors; return true; }
        // TreeView marks expanded nodes; do not enumerate unrelated disk branches.
        if (node.IsExpanded)
            foreach (var child in node.Items.Where(entry => entry.IsFolder))
                if (FindExpandedPath(child, target, new List<ExplorerEntry>(ancestors), out found)) return true;
        found = []; return false;
    }

    public void NavigateToDepth(int depth)
    {
        if (depth < 0 || depth >= path.Count - 1) return;
        back.Push(Address()); forward.Clear(); path.RemoveRange(depth + 1, path.Count - depth - 1);
        search = ""; Changed(nameof(Search)); Update();
    }
    public void GoBack()
    {
        if (!back.TryPop(out var address)) return;
        forward.Push(Address()); search = ""; Changed(nameof(Search)); RestorePath(address);
    }
    public void GoForward()
    {
        if (!forward.TryPop(out var address)) return;
        back.Push(Address()); search = ""; Changed(nameof(Search)); RestorePath(address);
    }
    public void Open(ExplorerEntry entry)
    {
        if (session.IsBusy) return;
        if (entry.IsFolder) { Navigate(entry); return; }
        if (entry.SceneId is { } scene) session.OpenScene(scene);
        else if (entry.ImageAssetId is { } image) session.SelectImage(image);
        else if (entry.CharacterId is { } character) session.SelectCharacter(character);
        else if (entry.ScreenId is { } screen) session.OpenScreenLibrary(screen);
        else if (entry.IsScreenLibrary) session.OpenScreenLibrary();
        else if (entry.ScriptId is { } script) _ = session.OpenScriptExternallyAsync(script);
        else if (entry.IsScriptLibrary) session.OpenScriptLibrary();
        else if (entry.IsProjectSettings) session.SelectedDocument = session.Documents.FirstOrDefault();
    }
    private void Update()
    {
        foreach (var ancestor in path.SkipLast(1)) ancestor.IsExpanded = true;
        Breadcrumbs.Clear();
        for (var i = 0; i < path.Count; i++) Breadcrumbs.Add(new(path[i].Label.TrimEnd('/'), i));
        Changed(nameof(CurrentFolder)); Changed(nameof(CurrentPath)); RefreshTiles();
        BackCommand.NotifyCanExecuteChanged(); ForwardCommand.NotifyCanExecuteChanged(); UpCommand.NotifyCanExecuteChanged();
    }
    private void RefreshTiles()
    {
        var selectedKey = selectedTile is null ? null : Key(selectedTile.Entry);
        Tiles.Clear();
        foreach (var tile in (CurrentFolder?.Items ?? []).Select(entry => new FolderTile(entry,
                     session.Project?.Images.FirstOrDefault(image => image.Id == entry.ImageAssetId), session.ProjectDirectory, session.AssetRevision))
                     .Where(tile => tile.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                    tile.Entry.Label.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(tile => tile.IsFolder).ThenBy(tile => tile.Name, StringComparer.OrdinalIgnoreCase))
            Tiles.Add(tile);
        SelectedTile = Tiles.FirstOrDefault(tile => Key(tile.Entry) == selectedKey);
        Changed(nameof(IsEmpty)); Changed(nameof(EmptyMessage)); Changed(nameof(SelectionDetail));
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public void Dispose() => session.Explorer.CollectionChanged -= ExplorerChanged;
}
