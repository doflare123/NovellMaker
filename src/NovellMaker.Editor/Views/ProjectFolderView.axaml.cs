using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

public sealed partial class ProjectFolderView : UserControl, IDisposable
{
    private EditorSession? session;
    private bool navigationResized;
    public ProjectFolderBrowser? Browser { get; private set; }
    public ProjectFolderView()
    {
        InitializeComponent();
        this.FindControl<GridSplitter>("FolderNavigationSplitter")!.PointerPressed += (_, _) => navigationResized = true;
        SizeChanged += (_, _) =>
        {
            if (!navigationResized && Bounds.Width > 0)
                this.FindControl<Grid>("BrowserColumns")!.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(Bounds.Width * .22, 80, 180));
        };
        this.FindControl<ListBox>("FolderTiles")!.AddHandler(KeyDownEvent, TileKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<TreeView>("FolderNavigation")!.AddHandler(KeyDownEvent, FolderKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not EditorSession value || ReferenceEquals(session, value)) return;
            Browser?.Dispose(); session = value; Browser = new(value);
            this.FindControl<Grid>("BrowserRoot")!.DataContext = Browser;
            Browser.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ProjectFolderBrowser.CurrentFolder))
                    this.FindControl<TreeView>("FolderNavigation")!.SelectedItem = Browser.CurrentFolder;
            };
        };
    }
    private void FolderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is TreeView { SelectedItem: ExplorerEntry folder }) Browser?.Navigate(folder);
    }
    private void BreadcrumbClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FolderBreadcrumb crumb }) Browser?.NavigateToDepth(crumb.Depth);
    }
    private void TileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control source && (source is ListBoxItem || source.FindAncestorOfType<ListBoxItem>() is not null) &&
            Browser?.SelectedTile is { } tile) { Browser.Open(tile.Entry); e.Handled = true; }
    }
    private async void TileKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Browser?.SelectedTile is { } tile) { Browser.Open(tile.Entry); e.Handled = true; }
        else if (e.Key == Key.Delete && Browser?.SelectedTile is { } selected)
        { e.Handled = true; var focus = e.Source as InputElement; if (session is not null) await session.DeleteExplorerEntryAsync(selected.Entry); focus?.Focus(); }
        else if (e.Key == Key.Back) { Browser?.UpCommand.Execute(null); e.Handled = true; }
    }
    private async void FolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && sender is TreeView { SelectedItem: ExplorerEntry entry } && session is not null)
        { e.Handled = true; var focus = e.Source as InputElement; await session.DeleteExplorerEntryAsync(entry); focus?.Focus(); }
    }
    public void Dispose() => Browser?.Dispose();
}
