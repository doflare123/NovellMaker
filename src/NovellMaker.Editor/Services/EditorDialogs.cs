using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace NovellMaker.Editor.Services;

public sealed class EditorDialogs(Window owner) : IEditorDialogs
{
    private static readonly FilePickerFileType ProjectType = new("Проект NovellMaker") { Patterns = ["*.novell"] };
    private static readonly FilePickerFileType ImageType = new("Изображение PNG или JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] };

    private sealed record StartScene(Guid Id, string Name) { public override string ToString() => Name; }

    public async Task<WindowsExportDestination?> PickWindowsExportAsync(NovellMaker.Core.Projects.NovelProject project)
    {
        var dialog = CreateDialog("Экспорт игры — Windows x64");
        var scenes = project.Scenes.Select(s => new StartScene(s.Id, s.Name)).ToArray();
        var picker = new ComboBox { Name = "ExportStartScene", ItemsSource = scenes, SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var cancel = new Button { Content = "Отмена" };
        var export = new Button { Content = "Выбрать папку и собрать…", Name = "ConfirmWindowsExport" };
        cancel.Click += (_, _) => dialog.Close(false);
        export.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children =
        {
            new TextBlock { Text = "Автономная игра в новой папке. Используется текущий снимок проекта, включая несохранённые изменения. Редактор и SDK игроку не нужны. Язык: " + project.DefaultLanguage, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "Стартовая сцена:" }, picker,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, export } }
        } };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(false); };
        if (!await dialog.ShowDialog<bool>(owner) || picker.SelectedItem is not StartScene scene) return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new() { Title = "Куда поместить папку игры?", AllowMultiple = false });
        if (folders.Count == 0) return null;
        return new(Path.Combine(LocalPath(folders[0]), $"Game-{project.Id.ToString("N")[..8]}-{Guid.NewGuid().ToString("N")[..8]}"), scene.Id);
    }

    public async Task<string?> PickImageAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Импортировать изображение", AllowMultiple = false, FileTypeFilter = [ImageType]
        });
        return files.Count > 0 ? LocalPath(files[0]) : null;
    }

    public async Task<string?> PickOpenProjectAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Открыть проект", AllowMultiple = false, FileTypeFilter = [ProjectType]
        });
        return files.Count > 0 ? LocalPath(files[0]) : null;
    }

    public async Task<string?> PickSaveProjectAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = "Сохранить проект", SuggestedFileName = suggestedName,
            DefaultExtension = "novell", FileTypeChoices = [ProjectType], ShowOverwritePrompt = true
        });
        return file is null ? null : LocalPath(file);
    }

    private static string LocalPath(IStorageItem item) => item.TryGetLocalPath()
        ?? throw new IOException("Выберите файл на локальном диске.");

    public async Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string projectName)
    {
        var dialog = CreateDialog("Несохранённые изменения");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var (text, result) in new[]
                 {
                     ("Сохранить", UnsavedChangesDecision.Save),
                     ("Не сохранять", UnsavedChangesDecision.Discard),
                     ("Отмена", UnsavedChangesDecision.Cancel)
                 })
        {
            var button = new Button { Content = text };
            button.Click += (_, _) => dialog.Close((UnsavedChangesDecision?)result);
            buttons.Children.Add(button);
        }
        dialog.Content = DialogContent($"Сохранить изменения проекта «{projectName}»?", buttons);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(); };
        return await dialog.ShowDialog<UnsavedChangesDecision?>(owner) ?? UnsavedChangesDecision.Cancel;
    }

    public async Task<bool> ConfirmDeleteAsync(string name, string consequences)
    {
        var dialog = CreateDialog("Подтверждение удаления");
        var cancel = new Button { Content = "Отмена", Name = "CancelDeleteButton" };
        var delete = new Button { Content = "Удалить", Name = "ConfirmDeleteButton" };
        cancel.Click += (_, _) => dialog.Close(false);
        delete.Click += (_, _) => dialog.Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Children = { cancel, delete } };
        dialog.Content = DialogContent($"Удалить «{name}»?\n\n{consequences}", buttons);
        dialog.Opened += (_, _) => cancel.Focus();
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(false); };
        return await dialog.ShowDialog<bool>(owner);
    }

    public Task ShowErrorAsync(string title, string message) => ShowMessageAsync(title, message);
    public Task ShowInformationAsync(string title, string message) => ShowMessageAsync(title, message);

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = CreateDialog(title);
        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = DialogContent(message, close);
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape || e.Key == Key.Enter) dialog.Close(); };
        await dialog.ShowDialog(owner);
    }

    private static Window CreateDialog(string title) => new()
    {
        Title = title, Width = 540, SizeToContent = SizeToContent.Height,
        MaxHeight = 600, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false
    };

    private static StackPanel DialogContent(string text, Control buttons) => new()
    {
        Margin = new Thickness(24), Spacing = 24,
        Children =
        {
            new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap } },
            buttons
        }
    };
}
