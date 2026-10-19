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

    public async Task<WindowsInstallerDestination?> PickWindowsInstallerAsync(NovellMaker.Core.Projects.NovelProject project)
    {
        var dialog = CreateDialog("Установщик игры EXE — Windows x64");
        var picker = new ComboBox { Name = "InstallerStartScene", ItemsSource = project.Scenes.Select(s => new StartScene(s.Id, s.Name)).ToArray(), SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var version = new TextBox { Name = "InstallerVersion", Text = "1.0.0" };
        var iconLabel = new TextBlock { Name = "InstallerIconPath", Text = "Стандартная иконка", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        string? iconPath = null;
        var icon = new Button { Content = "Выбрать ICO…", Name = "PickInstallerIcon" };
        icon.Click += async (_, _) =>
        {
            try
            {
                var icons = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "Иконка установщика и ярлыка", AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("Иконка Windows") { Patterns = ["*.ico"] }] });
                if (icons.Count > 0) { iconPath = LocalPath(icons[0]); iconLabel.Text = iconPath; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { iconLabel.Text = error.Message; }
        };
        var clearIcon = new Button { Content = "Сбросить" };
        clearIcon.Click += (_, _) => { iconPath = null; iconLabel.Text = "Стандартная иконка"; };
        var errorText = new TextBlock { Name = "InstallerValidation", Foreground = Avalonia.Media.Brushes.Orange, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var cancel = new Button { Content = "Отмена" };
        var export = new Button { Content = "Собрать EXE…", Name = "ConfirmInstallerExport" };
        cancel.Click += (_, _) => dialog.Close(false);
        export.Click += (_, _) =>
        {
            try { _ = new Export.WindowsInstallerOptions(version.Text ?? "").ValidateVersion(); dialog.Close(true); }
            catch (ArgumentException error) { errorText.Text = error.Message; }
        };
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 12, Children =
        {
            new TextBlock { Text = "Один EXE для передачи игроку: игра и .NET внутри. Установка для текущего пользователя, ярлык в меню «Пуск» и удаление через параметры Windows. Сохранения остаются при удалении. Используются текущие изменения проекта.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new TextBlock { Text = "Стартовая сцена:" }, picker,
            new TextBlock { Text = "Версия сборки (например 1.0.0):" }, version,
            new TextBlock { Text = "Иконка установщика и ярлыка (необязательно):" }, iconLabel,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, clearIcon } }, errorText,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, export } }
        } };
        dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) dialog.Close(false); };
        if (!await dialog.ShowDialog<bool>(owner) || picker.SelectedItem is not StartScene scene) return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new() { Title = "Сохранить установщик игры", SuggestedFileName = $"Game-{project.Id.ToString("N")[..8]}-Setup.exe",
            DefaultExtension = "exe", FileTypeChoices = [new FilePickerFileType("Установщик Windows") { Patterns = ["*.exe"] }], ShowOverwritePrompt = false });
        return file is null ? null : new(LocalPath(file), scene.Id, new(version.Text ?? "", iconPath));
    }

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

    public async Task<string?> PickAudioAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Импортировать звук PCM WAV", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PCM WAV 16 bit, mono/stereo, 8000–48000 Гц") { Patterns = ["*.wav"] }]
        });
        return files.Count > 0 ? LocalPath(files[0]) : null;
    }

    public async Task<string?> PickLuaScriptAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        { Title = "Импортировать Lua (UTF-8)", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Lua") { Patterns = ["*.lua"] }] });
        return files.Count > 0 ? LocalPath(files[0]) : null;
    }

    public async Task<string?> PickCodeEditorAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        { Title = "Открывать Lua в редакторе", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Приложение редактора") { Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"] }] });
        return files.Count > 0 ? LocalPath(files[0]) : null;
    }

    public async Task<string?> PickSaveLuaScriptAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new()
        { Title = "Выгрузить код в новый файл Lua", SuggestedFileName = suggestedName, DefaultExtension = "lua",
            FileTypeChoices = [new FilePickerFileType("Lua") { Patterns = ["*.lua"] }], ShowOverwritePrompt = false });
        return file is null ? null : LocalPath(file);
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
