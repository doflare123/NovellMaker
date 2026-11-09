using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace NovellMaker.Editor.Services;

public sealed partial class EditorDialogs
{
    public async Task<DialogueImportPlan?> ConfigureDialogueImportAsync(NovellMaker.Core.Projects.NovelProject project, Guid? sceneId, Guid? lineId, string language, string? projectDirectory)
    {
        var file = (await owner.StorageProvider.OpenFilePickerAsync(new() { Title = "Импорт персонажей и диалога", AllowMultiple = false,
            FileTypeFilter = [new("JSON, CSV, TSV") { Patterns = ["*.json", "*.csv", "*.tsv"] }] })).FirstOrDefault()?.TryGetLocalPath();
        if (file is null) return null;
        if (new FileInfo(file).Length > 32 * 1024 * 1024) throw new InvalidDataException("Файл импорта больше 32 МиБ.");
        var content = await File.ReadAllTextAsync(file);
        return await new Views.DialogueImportWindow(project, file, content, sceneId, lineId, language, projectDirectory).ShowDialog<DialogueImportPlan?>(owner);
    }
    private static readonly FilePickerFileType RecordingFiles = new("Лист записи CSV/TSV") { Patterns = ["*.csv", "*.tsv"] };
    public async Task<RecordingSheetScope?> PickRecordingSheetScopeAsync(int sceneCount, int filteredCount)
    {
        var dialog = CreateDialog("Лист записи: какие реплики выгрузить?");
        var all = new RadioButton { Name = "RecordingSheetAll", GroupName = "RecordingSheetScope", Content = $"Вся сцена ({sceneCount})", IsChecked = true };
        var filtered = new RadioButton { Name = "RecordingSheetFiltered", GroupName = "RecordingSheetScope", Content = $"Строки по фильтру ({filteredCount})", IsEnabled = filteredCount > 0 };
        var save = new Button { Name = "ContinueRecordingSheetExport", Content = "Продолжить", IsDefault = true, IsEnabled = sceneCount > 0 };
        save.Click += (_, _) => dialog.Close((RecordingSheetScope?)(filtered.IsChecked == true ? RecordingSheetScope.Filtered : RecordingSheetScope.Scene));
        var cancel = new Button { Content = "Отмена", IsCancel = true }; cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children = {
            new TextBlock { Text = "Язык и фильтры берутся из вкладки «Реплики». Номера строк остаются номерами полной сцены.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, all, filtered,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, save } } } };
        return await dialog.ShowDialog<RecordingSheetScope?>(owner);
    }
    public async Task<string?> PickSaveRecordingSheetAsync(string name) => (await owner.StorageProvider.SaveFilePickerAsync(new()
        { Title = "Экспорт листа записи", SuggestedFileName = name, FileTypeChoices = [RecordingFiles] }))?.TryGetLocalPath();
    public async Task<string?> PickRecordingSheetAsync() => (await owner.StorageProvider.OpenFilePickerAsync(new()
        { Title = "Лист записи CSV/TSV", AllowMultiple = false, FileTypeFilter = [RecordingFiles] })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<string?> PickVoiceFolderAsync() => (await owner.StorageProvider.OpenFolderPickerAsync(new()
        { Title = "Папка записей актёра", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    public async Task<bool> ConfirmVoiceImportAsync(VoiceImportPlan plan)
    {
        var dialog = CreateDialog("Импорт озвучки"); dialog.Width = 860; dialog.Height = 570; dialog.CanResize = true;
        var apply = new Button { Name = "ApplyVoiceBatch", Content = $"Применить записи: {plan.ImportCount}", IsEnabled = plan.ImportCount > 0 };
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
