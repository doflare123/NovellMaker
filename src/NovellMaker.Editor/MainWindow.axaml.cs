using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor;

public sealed partial class MainWindow : Window
{
    private bool allowClose;
    private bool closePending;
    public EditorSession Session { get; }

    public MainWindow() : this(null) { }

    public MainWindow(EditorSession? session)
    {
        InitializeComponent();
        Session = session ?? new EditorSession(new JsonProjectStore(), new EditorDialogs(this), new GitStatusService());
        DataContext = Session;
        AddHandler(LostFocusEvent, (_, _) => Session.EndEditGroup(), RoutingStrategies.Bubble);
        Closing += OnClosing;
        Activated += async (_, _) =>
        {
            if (Session.RefreshGitCommand.CanExecute(null)) await Session.RefreshGitAsync();
        };
    }

    private void ExitClicked(object? sender, RoutedEventArgs args) => Close();

    private void ExplorerSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is TreeView { SelectedItem: ExplorerEntry { SceneId: { } id } }) Session.OpenScene(id);
        else if (sender is TreeView { SelectedItem: ExplorerEntry { ImageAssetId: { } imageId } }) Session.SelectImage(imageId);
        else if (sender is TreeView { SelectedItem: ExplorerEntry { IsAssetLibrary: true } }) Session.OpenAssetLibrary();
        else if (sender is TreeView { SelectedItem: ExplorerEntry entry } && entry.SceneId is null &&
                 Session.Explorer.FirstOrDefault()?.Children.FirstOrDefault() == entry)
            Session.SelectedDocument = Session.Documents.FirstOrDefault();
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
