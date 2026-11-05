using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace NovellMaker.Editor.Services;

public sealed partial class EditorDialogs
{
    private static readonly FilePickerFileType RecordingFiles = new("Лист записи CSV/TSV") { Patterns = ["*.csv", "*.tsv"] };
    public async Task<string?> PickSaveRecordingSheetAsync(string name) => (await owner.StorageProvider.SaveFilePickerAsync(new()
        { Title = "Экспорт листа записи", SuggestedFileName = name, FileTypeChoices = [RecordingFiles] }))?.TryGetLocalPath();
    public async Task<string?> PickRecordingSheetAsync() => (await owner.StorageProvider.OpenFilePickerAsync(new()
        { Title = "Лист записи CSV/TSV", AllowMultiple = false, FileTypeFilter = [RecordingFiles] })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<string?> PickVoiceFolderAsync() => (await owner.StorageProvider.OpenFolderPickerAsync(new()
        { Title = "Папка записей актёра", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<bool> ConfirmVoiceImportAsync(VoiceImportPlan plan)
    {
        var dialog = CreateDialog("Импорт озвучки"); dialog.Width = 860; dialog.Height = 570; dialog.CanResize = true;
        var apply = new Button { Name = "ApplyVoiceBatch", Content = $"Применить {plan.Matches} записей", IsEnabled = plan.Matches > 0 };
        apply.Click += (_, _) => dialog.Close(true);
        var cancel = new Button { Content = "Отмена", IsCancel = true }; cancel.Click += (_, _) => dialog.Close(false);
        var panel = new DockPanel { Margin = new Thickness(20) };
        var header = new TextBlock { Text = plan.Summary + "\nСопоставление: ID фразы + язык. Применение отменяется одной операцией.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, apply } };
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ListBox { ItemsSource = plan.Items, ItemTemplate = new FuncDataTemplate<VoiceImportItem>((item, _) => new TextBlock { Text = item?.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap }) });
        dialog.Content = panel;
        return await dialog.ShowDialog<bool>(owner);
    }
}
