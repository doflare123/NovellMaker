using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Projects;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.Views;

/// <summary>Builds an immutable plan. Nothing is copied or written until its result is applied by the session.</summary>
public sealed class DialogueImportWindow : Window
{
    private sealed record Column(string? Header) { public override string ToString() => Header ?? "Не использовать"; }
    private sealed record Separator(string Value, string Name) { public override string ToString() => Name; }
    private readonly NovelProject project;
    private readonly Guid? lineId;
    private readonly string? projectDirectory;
    private readonly string content;
    private readonly bool json;
    private readonly TextBox sourceKey = new() { Name = "DialogueImportSourceKey" }, root = new() { Name = "DialogueImportRoot" }, language = new() { Name = "DialogueImportLanguage", Width = 85 };
    private readonly ComboBox separator = new() { Name = "DialogueImportSeparator", Width = 170 };
    private readonly ComboBox profilePicker = new() { Name = "DialogueImportProfilePicker", Width = 180, PlaceholderText = "Профиль колонок" };
    private readonly ComboBox destination = new() { Name = "DialogueImportDestination", MinWidth = 200 };
    private readonly CheckBox saveProfile = new() { Content = "Сохранить профиль", Name = "SaveDialogueImportProfile" };
    private readonly TextBox profileName = new() { Name = "DialogueImportProfileName", Width = 170 };
    private readonly CheckBox update = new() { Content = "Обновлять по внешним ключам", IsChecked = true, Name = "DialogueImportUpdate" };
    private readonly CheckBox voices = new() { Content = "Импортировать WAV", IsChecked = true, Name = "DialogueImportVoices" };
    private readonly CheckBox images = new() { Content = "Импортировать изображения", IsChecked = true, Name = "DialogueImportImages" };
    private readonly WrapPanel columnsPanel = new(), charactersPanel = new();
    private readonly Dictionary<string, ComboBox> columns = new(), targets = new();
    private readonly TextBlock status = new() { Name = "DialogueImportStatus", TextWrapping = TextWrapping.Wrap };
    private readonly ListBox preview = new() { Name = "DialogueImportPreview", MinHeight = 100 };
    private readonly Button apply = new() { Content = "Применить импорт", Name = "ApplyDialogueImport", IsEnabled = false };
    private readonly Button prepare = new() { Content = "Предпросмотр", Name = "PreviewDialogueImport" };
    private DialogueImportPlan? plan;
    private bool refreshing, preparing;
    private int settingsRevision;
    private string? targetSource;
    private static readonly (string Key, string Label)[] Fields = [("text", "Текст"), ("speaker", "Персонаж"), ("characterKey", "Ключ персонажа"),
        ("lineKey", "Ключ реплики"), ("language", "Язык"), ("voice", "WAV"), ("image", "Изображение"), ("pose", "Поза"), ("scene", "Сцена"), ("sceneKey", "Ключ сцены")];

    public DialogueImportWindow(NovelProject project, string path, string content, Guid? sceneId, Guid? lineId, string language, string? projectDirectory = null)
    {
        this.project = project; this.content = content; this.lineId = lineId; this.projectDirectory = projectDirectory;
        json = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
        Title = "Импорт персонажей и диалога"; Width = 1040; Height = 820; MinWidth = 700; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        sourceKey.Text = Path.GetFileNameWithoutExtension(path); root.Text = Path.GetDirectoryName(Path.GetFullPath(path)); this.language.Text = language;
        profileName.Text = sourceKey.Text;
        separator.ItemsSource = new[] { new Separator(",", "Запятая"), new Separator(";", "Точка с запятой"), new Separator("\t", "Табуляция") };
        separator.SelectedIndex = DialogueImportReader.DetectSeparator(content, path) switch { ";" => 1, "\t" => 2, _ => 0 };
        var panel = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true }; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel); buttons.Children.Add(prepare); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Path.GetFileName(path) + (json ? " · JSON v1" : " · CSV/TSV"), FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = "Источник — постоянное имя набора. При повторном импорте используйте то же имя: ключи сохранят ID. Строки без ключей добавляются заново. Существующий порядок сохраняется.", TextWrapping = TextWrapping.Wrap });
        var settings = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2*,Auto"), ColumnSpacing = 10 };
        settings.Children.Add(Labeled("Источник", sourceKey)); var rootPanel = Labeled("Папка ресурсов (относительные пути из файла)", root); Grid.SetColumn(rootPanel, 1); settings.Children.Add(rootPanel);
        var languagePanel = Labeled("Язык по умолчанию", this.language); Grid.SetColumn(languagePanel, 2); settings.Children.Add(languagePanel); body.Children.Add(settings);
        var flags = new WrapPanel(); foreach (var control in new Control[] { update, voices, images }) { control.Margin = new Thickness(0, 0, 16, 0); flags.Children.Add(control); } body.Children.Add(flags);
        var destinations = new[] { new ImportTarget(null, true, "Создать новую сцену") }.Concat(project.Scenes.Select(s => new ImportTarget(s.Id, false, s.Name))).ToArray();
        destination.ItemsSource = destinations; destination.SelectedItem = destinations.FirstOrDefault(d => d.Id == sceneId) ?? destinations[0];
        body.Children.Add(Labeled("Реплики без ключа сцены — назначение", destination)); destination.SelectionChanged += (_, _) => InvalidatePlan();
        var tableSettings = new StackPanel { Spacing = 8, IsVisible = !json };
        profilePicker.ItemsSource = project.Imports.Profiles;
        profilePicker.ItemTemplate = new FuncDataTemplate<DialogueImportProfile>((p, _) => new TextBlock { Text = p?.Name });
        var loadProfile = new Button { Content = "Загрузить профиль", Name = "LoadDialogueImportProfile" }; loadProfile.Click += (_, _) => LoadProfile();
        var profiles = new WrapPanel(); foreach (var control in new Control[] { separator, profilePicker, loadProfile, saveProfile, profileName }) { control.Margin = new Thickness(0, 0, 8, 4); profiles.Children.Add(control); }
        tableSettings.Children.Add(profiles); tableSettings.Children.Add(columnsPanel); body.Children.Add(tableSettings);
        var parse = new Button { Content = "Разобрать и сопоставить персонажей", Name = "ParseDialogueImport" };
        parse.Click += (_, _) => TryParse(); body.Children.Add(parse);
        body.Children.Add(charactersPanel); body.Children.Add(status); body.Children.Add(preview);
        panel.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }); Content = panel;
        foreach (var box in new[] { sourceKey, root, this.language, profileName }) box.TextChanged += (_, _) => InvalidatePlan();
        foreach (var check in new[] { update, voices, images, saveProfile }) check.IsCheckedChanged += (_, _) => InvalidatePlan();
        separator.SelectionChanged += (_, _) => { if (!refreshing) { SetupColumns(null); InvalidatePlan(); } };
        prepare.Click += async (_, _) => await PrepareAsync();
        apply.Click += (_, _) => { if (plan?.CanApply == true) Close(plan); };
        SetupColumns(null); TryParse();
    }
    private static StackPanel Labeled(string label, Control field) => new() { Spacing = 3, Children = { new TextBlock { Text = label }, field } };
    private string SeparatorValue => (separator.SelectedItem as Separator)?.Value ?? ",";
    private void InvalidatePlan() { if (refreshing) return; settingsRevision++; plan = null; apply.IsEnabled = false; preview.ItemsSource = null; status.Text = "Параметры изменены. Проверьте предпросмотр."; }
    private void SetupColumns(ImmutableDictionary<string, string>? selected)
    {
        if (json) return;
        try
        {
            refreshing = true; columns.Clear(); columnsPanel.Children.Clear();
            var table = DialogueImportReader.ReadTable(content, SeparatorValue); selected ??= DialogueImportReader.SuggestColumns(table);
            var options = new[] { new Column(null) }.Concat(table.Headers.Select(h => new Column(h))).ToArray();
            foreach (var field in Fields)
            {
                var picker = new ComboBox { Name = "ImportColumn_" + field.Key, ItemsSource = options, Width = 170 };
                picker.SelectedItem = options.FirstOrDefault(o => o.Header == selected.GetValueOrDefault(field.Key)) ?? options[0];
                picker.SelectionChanged += (_, _) => InvalidatePlan(); columns[field.Key] = picker;
                var block = Labeled(field.Label, picker); block.Margin = new Thickness(0, 0, 10, 6); columnsPanel.Children.Add(block);
            }
            status.Text = $"Строк в таблице: {table.Rows.Count}. Для импорта только персонажей отключите колонку текста.";
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException) { status.Text = e.Message; }
        finally { refreshing = false; }
    }
    private DialogueImportProfile Profile(string name) => new(name, SeparatorValue, language.Text ?? "", columns
        .Where(p => (p.Value.SelectedItem as Column)?.Header is not null).ToImmutableDictionary(p => p.Key, p => ((Column)p.Value.SelectedItem!).Header!));
    private DialogueImportSource ReadSource() => json ? DialogueImportReader.ReadJson(content) : DialogueImportReader.FromTable(DialogueImportReader.ReadTable(content, SeparatorValue), Profile("preview"));
    private DialogueImportSource ParseCharacters()
    {
        var source = ReadSource(); var parsed = DialogueImportPlanner.Characters(source);
        var previous = targetSource == sourceKey.Text ? targets.ToDictionary(p => p.Key, p => p.Value.SelectedItem as ImportTarget) : new();
        targetSource = sourceKey.Text; targets.Clear(); charactersPanel.Children.Clear();
        foreach (var character in parsed)
        {
            var options = new[] { new ImportTarget(null, true, "Создать: " + character.Name), new(null, false, "Рассказчик / без персонажа") }
                .Concat(project.Characters.Select(c => new ImportTarget(c.Id, false, c.Name))).ToArray();
            var picker = new ComboBox { Name = "ImportCharacter_" + character.Key, Width = 240, ItemsSource = options };
            var suggested = previous.GetValueOrDefault(character.Key) ?? DialogueImportPlanner.SuggestTarget(project, sourceKey.Text ?? "", character);
            picker.SelectedItem = suggested is null ? null : options.FirstOrDefault(o => o.Id == suggested.Id && o.Create == suggested.Create);
            picker.SelectionChanged += (_, _) => InvalidatePlan(); targets[character.Key] = picker;
            var block = Labeled(character.Name + " [" + character.Key + "]", picker); block.Margin = new Thickness(0, 0, 10, 8); charactersPanel.Children.Add(block);
        }
        return source;
    }
    private void TryParse()
    {
        InvalidatePlan();
        try { var source = ParseCharacters(); status.Text = $"Персонажей: {targets.Count}. Проверьте сопоставления и нажмите «Предпросмотр»."; }
        catch (Exception e) when (e is not OutOfMemoryException) { status.Text = e.Message; }
    }
    private void LoadProfile()
    {
        if (profilePicker.SelectedItem is not DialogueImportProfile profile) return;
        refreshing = true; separator.SelectedIndex = profile.Separator switch { ";" => 1, "\t" => 2, _ => 0 }; language.Text = profile.Language; profileName.Text = profile.Name; refreshing = false;
        SetupColumns(profile.Columns); TryParse();
    }
    private async Task PrepareAsync()
    {
        if (preparing) return;
        InvalidatePlan(); preparing = true; prepare.IsEnabled = false;
        try
        {
            var source = ParseCharacters();
            if (targets.Any(t => t.Value.SelectedItem is not ImportTarget)) throw new InvalidDataException("Сопоставьте неоднозначные имена персонажей.");
            var options = new DialogueImportOptions(sourceKey.Text ?? "", root.Text ?? "", language.Text ?? "", (destination.SelectedItem as ImportTarget)?.Id, lineId,
                update.IsChecked == true, voices.IsChecked == true, images.IsChecked == true,
                targets.ToImmutableDictionary(p => p.Key, p => (ImportTarget)p.Value.SelectedItem!), !json && saveProfile.IsChecked == true ? Profile(profileName.Text ?? "") : null, projectDirectory);
            var revision = settingsRevision;
            var prepared = await DialogueImportPlanner.PrepareAsync(project, source, options);
            if (revision != settingsRevision) return;
            plan = prepared;
            status.Text = plan.Summary;
            preview.ItemsSource = plan.Errors.Select(e => "ОШИБКА: " + e).Concat(plan.Changes).ToArray();
            preview.ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap });
            apply.IsEnabled = plan.CanApply;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { plan = null; apply.IsEnabled = false; status.Text = e.Message; }
        finally { preparing = false; prepare.IsEnabled = true; }
    }
}
