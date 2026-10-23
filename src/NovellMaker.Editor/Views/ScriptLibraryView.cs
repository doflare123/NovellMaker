using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Execution;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

/// <summary>Script bindings and diagnostics; source editing belongs to the configured external application.</summary>
public sealed class ScriptLibraryView : UserControl
{
    private readonly ListBox scripts = new() { Name = "LuaScriptList", MinWidth = 160 };
    private readonly TextBox name = new() { Name = "LuaScriptName" };
    private readonly TextBlock path = new() { Name = "LuaScriptPath", TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private readonly TextBlock editorName = new() { Name = "CodeEditorName", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Name = "LuaAssetStatus", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox targets = new() { Name = "LuaAssetReferenceTarget", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox scenes = new() { Name = "LuaScriptScene", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox alias = new() { Name = "LuaAssetReferenceAlias", Text = "actor", PlaceholderText = "Имя ссылки" };
    private readonly TextBlock references = new() { Name = "LuaAssetReferences", TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel inspector = new() { Spacing = 8 };
    private readonly CheckBox autoOpen = new() { Name = "OpenCodeOnCreation", Content = "Открывать код после создания" };
    private readonly List<Button> editButtons = [];
    private EditorSession? session;
    private bool refreshing;
    private Guid? shownId;
    private sealed record Target(Guid Id, string Label) { public override string ToString() => Label; }

    public ScriptLibraryView() : this(false) { }
    public ScriptLibraryView(bool propertiesOnly)
    {
        ToolTip.SetTip(references, "Ссылка на Lua-ресурс доступна в обработчике как modules.<имя>: ui.import(modules.counter). refs узлов задаются у холста. Сценарный game.run(refs.<имя>) сохраняет прежний API.");
        var root = new DockPanel { Margin = new Thickness(12) };
        var toolbar = new WrapPanel();
        void Button(string label, string id, Func<Task> run)
        {
            var button = new Button { Content = label, Name = id, Margin = new Thickness(0, 0, 6, 6) };
            button.Click += async (_, _) => { status.Text = ""; try { await run(); }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { status.Text = ex.Message; } };
            toolbar.Children.Add(button); editButtons.Add(button);
        }
        Button("Создать", "CreateLuaScript", () => session?.CreateAndOpenScriptAsync() ?? Task.CompletedTask);
        Button("Импорт .lua…", "ImportLuaScript", () => session?.ImportScriptAsync() ?? Task.CompletedTask);
        Button("Открыть код", "OpenLuaScriptExternally", () => session?.OpenScriptExternallyAsync() ?? Task.CompletedTask);
        Button("Удалить", "DeleteLuaScript", () => session?.SelectedScript is { } script ? session.DeleteScriptAsync(script.Id) : Task.CompletedTask);
        if (!propertiesOnly) { DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar); }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("160,10,*") };
        scripts.ItemTemplate = new FuncDataTemplate<LuaScriptAsset>((item, _) => new TextBlock { Text = item?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!propertiesOnly) grid.Children.Add(scripts);
        inspector.Children.Add(new TextBlock { Text = "Имя скрипта (можно изменить)" }); inspector.Children.Add(name);
        inspector.Children.Add(path);
        inspector.Children.Add(status);
        var editorSettings = new StackPanel { Spacing = 6 };
        editorSettings.Children.Add(editorName);
        var choose = new Button { Name = "ChooseCodeEditor", Content = "Выбрать приложение…" };
        choose.Click += async (_, _) => { if (session is not null) await session.ChooseCodeEditorAsync(); };
        var system = new Button { Name = "UseSystemCodeEditor", Content = "Системное приложение" };
        system.Click += async (_, _) => { if (session is not null) await session.UseSystemCodeEditorAsync(); };
        editorSettings.Children.Add(choose); editorSettings.Children.Add(system);
        autoOpen.IsCheckedChanged += async (_, _) => { if (!refreshing && session is not null) await session.SetOpenCodeOnCreationAsync(autoOpen.IsChecked == true); };
        autoOpen.DataContextChanged += (_, _) => autoOpen.IsChecked = (autoOpen.DataContext as EditorSession)?.OpenCodeOnCreation == true;
        editorSettings.Children.Add(autoOpen);
        inspector.Children.Add(new Expander { Name = "CodeEditorSettings", Header = "Открывать по умолчанию", Content = editorSettings });
        inspector.Children.Add(new HelpTip { Explanation = "Код открывается во внешнем редакторе. Сохранения файлов проверяются автоматически. F5 — обновить вручную." });
        var referencePanel = new StackPanel { Spacing = 6 };
        referencePanel.Children.Add(targets); referencePanel.Children.Add(alias);
        inspector.Children.Add(new Expander { Name = "LuaReferencesSettings", Header = "Именованные ссылки", Content = referencePanel });
        var insert = new Button { Name = "InsertLuaAssetReference", Content = "Добавить ссылку" };
        insert.Click += (_, _) =>
        {
            if (session?.SelectedScript is not { } script || targets.SelectedItem is not Target target) return;
            var key = alias.Text ?? "";
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$"))
            { status.Text = "Имя ссылки: латинская буква или подчёркивание, затем буквы, цифры и подчёркивания."; return; }
            session.UpdateScript(script with { References = script.References.SetItem(key, target.Id) });
        };
        referencePanel.Children.Add(insert); referencePanel.Children.Add(references);
        var copy = new Button { Name = "CopyLuaAssetReference", Content = "Копировать refs" };
        copy.Click += async (_, _) => { if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync("refs." + alias.Text); };
        referencePanel.Children.Add(copy);
        var removeReference = new Button { Name = "RemoveLuaAssetReference", Content = "Удалить ссылку с указанным именем" };
        removeReference.Click += (_, _) =>
        {
            if (session?.SelectedScript is { } script) session.UpdateScript(script with { References = script.References.Remove(alias.Text ?? "") });
        };
        referencePanel.Children.Add(removeReference);
        inspector.Children.Add(new TextBlock { Text = "Запуск в начале сцены" }); inspector.Children.Add(scenes);
        var attach = new Button { Name = "AttachLuaScriptToScene", Content = "Добавить вызов в начало сцены" };
        attach.Click += (_, _) =>
        {
            try
            {
                if (session?.SelectedScript is { } script && scenes.SelectedItem is Target scene)
                { session.AttachScriptToSceneStart(scene.Id, script.Id); }
            }
            catch (ArgumentException error) { status.Text = error.Message; }
        };
        inspector.Children.Add(attach);
        var scroll = new ScrollViewer { Name = "LuaScriptInspector", Content = inspector, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!propertiesOnly) { Grid.SetColumn(scroll, 2); grid.Children.Add(scroll); root.Children.Add(grid); }
        else root.Children.Add(scroll);
        Content = root;
        scripts.SelectionChanged += (_, _) => { if (!refreshing) session?.SelectScript((scripts.SelectedItem as LuaScriptAsset)?.Id); };
        scripts.DoubleTapped += async (_, _) => { if (session?.SelectedScript is not null) await session.OpenScriptExternallyAsync(); };
        // TextChanged is queued by Avalonia. Update synchronously so a refresh for another field cannot discard pending input.
        name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Edit(s => s with { Name = name.Text ?? "" }, "name"); };
        name.LostFocus += (_, _) => session?.EndEditGroup();
        DataContextChanged += (_, _) =>
        {
            if (session is not null) session.PropertyChanged -= Changed;
            session = DataContext as EditorSession;
            if (session is not null) session.PropertyChanged += Changed;
            Refresh();
        };
        AttachedToVisualTree += (_, _) => { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } Refresh(); };
        DetachedFromVisualTree += (_, _) => { if (session is not null) session.PropertyChanged -= Changed; };
    }

    private void Edit(Func<LuaScriptAsset, LuaScriptAsset> update, string field)
    {
        if (refreshing || session?.SelectedScript is not { } script) return;
        try { session.UpdateScript(update(script), $"lua:{script.Id}:{field}"); status.Text = ""; }
        catch (ArgumentException ex) { status.Text = ex.Message; }
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorSession.SelectedScriptDiagnostic)) { status.Text = session?.SelectedScriptDiagnostic; return; }
        if (e.PropertyName is nameof(EditorSession.SelectedScript) or nameof(EditorSession.CanEdit) or nameof(EditorSession.CodeEditorLabel)
            or nameof(EditorSession.SelectedScriptDiagnostic) or nameof(EditorSession.SelectedScriptPath) or nameof(EditorSession.OpenCodeOnCreation)) Refresh();
    }

    private void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var script = session?.SelectedScript;
            autoOpen.IsChecked = session?.OpenCodeOnCreation == true;
            if (shownId != script?.Id) { status.Text = ""; shownId = script?.Id; }
            scripts.ItemsSource = session?.Project?.Scripts.ToArray() ?? [];
            scripts.SelectedItem = script;
            if (name.Text != (script?.Name ?? "")) name.Text = script?.Name ?? "";
            path.Text = session?.SelectedScriptPath; editorName.Text = session?.CodeEditorLabel;
            status.Text = session?.SelectedScriptDiagnostic;
            name.IsEnabled = session?.CanEdit == true && script is not null;
            scripts.IsEnabled = session?.CanEdit == true;
            foreach (var button in editButtons) button.IsEnabled = session?.CanEdit == true &&
                (button.Name is "CreateLuaScript" or "ImportLuaScript" || script is not null);
            var oldTarget = (targets.SelectedItem as Target)?.Id;
            var oldScene = (scenes.SelectedItem as Target)?.Id;
            var options = ReferenceTargets().ToArray(); targets.ItemsSource = options;
            references.Text = script is null ? "" : string.Join("\n", script.References.Select(r =>
                $"refs.{r.Key}" + (session?.Project?.Scripts.Any(s => s.Id == r.Value) == true ? $" / modules.{r.Key}" : "") +
                $" → {options.FirstOrDefault(t => t.Id == r.Value)?.Label ?? "Объект удалён"}"));
            targets.SelectedItem = options.FirstOrDefault(t => t.Id == oldTarget) ?? options.FirstOrDefault();
            var sceneOptions = session?.Project?.Scenes.Where(s => !s.HasLegacyActionScript).Select(s => new Target(s.Id, s.Name)).ToArray() ?? [];
            scenes.ItemsSource = sceneOptions; scenes.SelectedItem = sceneOptions.FirstOrDefault(t => t.Id == oldScene) ?? sceneOptions.FirstOrDefault();
        }
        finally { refreshing = false; }
    }

    private IEnumerable<Target> ReferenceTargets()
    {
        if (session?.Project is not { } project) yield break;
        foreach (var character in project.Characters)
        {
            yield return new(character.Id, character.Name);
            foreach (var pose in character.States) yield return new(pose.Id, character.Name + " / " + pose.Name);
        }
        foreach (var scene in project.Scenes)
        {
            yield return new(scene.Id, "Сцена: " + scene.Name);
            foreach (var line in scene.Lines) yield return new(line.Id, scene.Name + " / " + line.Texts.GetValueOrDefault(project.DefaultLanguage, "Пустая фраза"));
            foreach (var hotspot in scene.Actions.Concat(scene.ScriptBlocks.Select(b => b.Action)).OfType<HotspotAction>()) yield return new(hotspot.ObjectId, "Предмет: " + hotspot.Label);
        }
        foreach (var script in project.Scripts) yield return new(script.Id, "Lua: " + script.Name);
    }
}
