using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;
using NovellMaker.Editor.Views;

namespace NovellMaker.Editor;

public sealed partial class MainWindow : Window
{
    internal WorkspaceDock WorkspaceLayout { get; }
    private bool allowClose;
    private bool closePending;
    public EditorSession Session { get; }

    public MainWindow() : this(null) { }

    public MainWindow(EditorSession? session)
    {
        InitializeComponent();
        Session = session ?? new EditorSession(new JsonProjectStore(), new EditorDialogs(this), new GitStatusService());
        DataContext = Session;
        var workspace = this.FindControl<Grid>("Workspace")!;
        var contents = new[] { "ExplorerPane", "DocumentsPane", "InformationPane", "MessagesPane", "ScenePane" }
            .Select(name => this.FindControl<Control>(name)!).ToArray();
        workspace.Children.Clear();
        WorkspaceLayout = new WorkspaceDock(workspace, this.FindControl<MenuItem>("WorkspacePanelsMenu")!);
        WorkspaceLayout.Register("explorer", "Обозреватель проекта", contents[0]);
        WorkspaceLayout.Register("documents", "Документы", contents[1]);
        WorkspaceLayout.Register("information", "Свойства", contents[2]);
        WorkspaceLayout.Register("messages", "Сообщения", contents[3]);
        WorkspaceLayout.Register("scene", "Экран сцены", contents[4]);
        WorkspaceLayout.Reset();
        Session.PropertyChanged += ShowInspectedProperties;
        var layoutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovellMaker", "workspace.json");
        if (session is null && File.Exists(layoutPath))
        {
            try { WorkspaceLayout.ImportLayout(File.ReadAllText(layoutPath)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { Session.Diagnostics.Insert(0, "Не удалось восстановить расположение панелей: " + error.Message); }
        }
        AddHandler(LostFocusEvent, (_, _) => Session.EndEditGroup(), RoutingStrategies.Bubble);
        this.FindControl<TreeView>("ProjectExplorer")!.AddHandler(KeyDownEvent, ExplorerKeyDown, RoutingStrategies.Tunnel);
        var explorer = this.FindControl<TreeView>("ProjectExplorer")!;
        ScriptDragDrop.Source(explorer, () => Session);
        ScriptDragDrop.Destination(explorer, () => Session, e => ScriptDragDrop.Entry(e.Source)?.SceneId is { } id ? new(id, null, NovellMaker.Core.Scenes.ScriptPhase.SceneStart) : null);
        explorer.ContextMenu = ScriptExplorerMenu.Create(Session, () => explorer.SelectedItem as ExplorerEntry);
        explorer.DoubleTapped += async (_, _) =>
        {
            if (explorer.SelectedItem is ExplorerEntry { ScriptId: { } scriptId })
                await Session.OpenScriptExternallyAsync(scriptId);
        };
        explorer.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(explorer).Properties.IsRightButtonPressed && e.Source is Control control &&
                control.GetSelfAndVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext is ExplorerEntry entry)
                explorer.SelectedItem = entry;
        }, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Session.PropertyChanged -= ShowInspectedProperties;
            if (session is null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);
                    File.WriteAllText(layoutPath + ".tmp", WorkspaceLayout.ExportLayout());
                    File.Move(layoutPath + ".tmp", layoutPath, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Session.Diagnostics.Insert(0, "Не удалось сохранить расположение панелей: " + error.Message); }
            }
            this.FindControl<ProjectFolderView>("ProjectFolders")?.Dispose();
            Session.Dispose();
        };
        Activated += async (_, _) =>
        {
            if (Session.RefreshGitCommand.CanExecute(null)) await Session.RefreshGitAsync();
        };
    }

    private void ResetWorkspaceClicked(object? sender, RoutedEventArgs args) => WorkspaceLayout.Reset();

    private void ShowInspectedProperties(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Session.HasProject && e.PropertyName == nameof(EditorSession.InspectorTarget) && !WorkspaceLayout.IsVisible("information"))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (Session.HasProject) WorkspaceLayout.Show("information"); });
    }

    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();

    private void ExplorerSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is TreeView { SelectedItem: ExplorerEntry { SceneId: { } id } }) { Session.OpenScene(id); Session.InspectPhrase(); }
        else if (sender is TreeView { SelectedItem: ExplorerEntry { ImageAssetId: { } imageId } }) Session.SelectImage(imageId);
        else if (sender is TreeView { SelectedItem: ExplorerEntry { IsAssetLibrary: true } }) Session.OpenAssetLibrary();
        else if (sender is TreeView { SelectedItem: ExplorerEntry { CharacterId: { } characterId } }) Session.SelectCharacter(characterId);
        else if (sender is TreeView { SelectedItem: ExplorerEntry { IsCharacterLibrary: true } }) Session.OpenCharacterLibrary();
        else if (sender is TreeView { SelectedItem: ExplorerEntry { ScriptId: { } scriptId } }) Session.InspectScript(scriptId);
        else if (sender is TreeView { SelectedItem: ExplorerEntry { IsScriptLibrary: true } }) Session.OpenScriptLibrary();
        else if (sender is TreeView { SelectedItem: ExplorerEntry { IsProjectSettings: true } })
            Session.SelectedDocument = Session.Documents.FirstOrDefault();
    }

    private async void ExplorerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && sender is TreeView { SelectedItem: ExplorerEntry { ScriptId: { } scriptId } })
        { e.Handled = true; await Session.RenameScriptAsync(scriptId); return; }
        if (e.Key != Key.Delete || e.Source is Control source && source.GetSelfAndVisualAncestors().OfType<TextBox>().Any()) return;
        if (sender is TreeView { SelectedItem: ExplorerEntry entry })
        {
            e.Handled = true;
            var focus = e.Source as InputElement;
            await Session.DeleteExplorerEntryAsync(entry);
            focus?.Focus();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose) return;
        if (!Session.IsBusy && !Session.IsDirty) return;
        args.Cancel = true;
        if (closePending) return;
        closePending = true;
        try
        {
            // Let Avalonia finish cancelling this Closing event before issuing a new Close.
            await Task.Yield();
            if (await Session.RequestCloseAsync())
            {
                allowClose = true;
                Close();
            }
        }
        finally { closePending = false; }
    }
}
