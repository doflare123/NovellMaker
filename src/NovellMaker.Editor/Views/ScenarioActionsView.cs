using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

/// <summary>Phrase-oriented script: readable blocks and Lua share stable cue bindings.</summary>
public sealed class ScenarioActionsView : UserControl
{
    public bool PropertiesOnly { get; }
    private readonly ListBox list = new() { Name = "ScriptBlocks", MinWidth = 170 };
    private readonly ComboBox anchors = new() { Name = "ScriptPhrasePicker", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox phases = new() { Name = "ScriptPhasePicker", Width = 190 };
    private readonly StackPanel form = new() { Spacing = 7 };
    private readonly TextBlock context = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6) };
    private readonly TextBlock error = new() { Name = "ScriptError", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SandyBrown };
    private readonly ScrollViewer details = new() { Name = "ScriptInspectorScroll" };
    private SceneDocumentViewModel? document;
    private Guid? selectedId;
    private bool refreshing;
    private Func<JsonNode?>? readDraft;
    private NovelScene? Scene => document?.Session.Project?.Scenes.FirstOrDefault(s => s.Id == document.Id);
    private sealed record Anchor(Guid? LineId, bool End, string Label) { public override string ToString() => Label; }
    private sealed record Phase(ScriptPhase Value, string Label) { public override string ToString() => Label; }
    private sealed record Row(ScenarioAction Action, ScriptBlock? Block, string Title, string Detail);
    private sealed record Reference(Guid? Id, string Label) { public override string ToString() => Label; }
    private sealed record WaitMode(string Value, string Label) { public override string ToString() => Label; }
    private static readonly Phase[] LinePhases = [new(ScriptPhase.BeforeLine, "До фразы"), new(ScriptPhase.DuringLine, "Во время фразы"), new(ScriptPhase.AfterLine, "После продолжения")];
    private static readonly WaitMode[] WaitModes = [new("input", "Продолжение игроком"), new("timer", "Заданное время"), new("actions", "Завершение движений"), new("interaction", "Щелчок по предмету")];

    public ScenarioActionsView() : this(false) { }
    public ScenarioActionsView(bool propertiesOnly, bool actionOnly = false)
    {
        PropertiesOnly = propertiesOnly;
        ToolTip.SetTip(this, "Перетащите Lua-скрипт из обозревателя, чтобы прикрепить его к выбранному событию.");
        ScriptDragDrop.Destination(this, () => document?.Session, _ => Scene is { } s && anchors.SelectedItem is Anchor a && phases.SelectedItem is Phase phase ? new(s.Id, a.LineId, phase.Value) : null);
        var root = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var top = new StackPanel { Spacing = 6 };
        // Explanations belong in tooltips, not a permanent paragraph above every event.
        var scope = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,190") };
        scope.Children.Add(anchors);
        if (propertiesOnly)
        { scope.ColumnDefinitions = new ColumnDefinitions("*"); scope.RowDefinitions = new RowDefinitions("Auto,Auto"); phases.Width = double.NaN; phases.HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetRow(phases, 1); }
        else Grid.SetColumn(phases, 2);
        scope.Children.Add(phases); top.Children.Add(scope);
        string[] kinds = ["Двигать персонажа", "Показать персонажа", "Скрыть персонажа", "Сменить состояние", "Разместить персонажа", "Ожидать", "Предложить выбор", "Перейти к фразе", "Записать переменную", "Проверить условие", "Добавить предмет"];
        ContextMenu CreateMenu()
        {
            var menu = new ContextMenu();
            var addMenu = new MenuItem { Header = "Добавить действие" };
            for (var i = 0; i < kinds.Length; i++)
            {
                var index = i; var item = new MenuItem { Header = kinds[i] };
                item.Click += (_, _) => Add(index); addMenu.Items.Add(item);
            }
            menu.Items.Add(addMenu);
            var lua = new MenuItem { Header = "Создать Lua-скрипт…", Name = "CreatePhraseLuaMenu" }; lua.Click += (_, _) => Add(11); menu.Items.Add(lua);
            var call = new MenuItem { Header = "Вызвать существующий скрипт" }; call.Click += (_, _) => Add(12); menu.Items.Add(call);
            var delete = new MenuItem { Header = "Удалить" }; delete.Click += async (_, _) =>
            { if (Scene is { } s && document is not null && selectedId is { } id) await document.Session.DeleteActionAsync(s.Id, id); }; menu.Items.Add(delete);
            var up = new MenuItem { Header = "Выше" }; up.Click += (_, _) => Move(-1); menu.Items.Add(up);
            var down = new MenuItem { Header = "Ниже" }; down.Click += (_, _) => Move(1); menu.Items.Add(down);
            var properties = new MenuItem { Header = "Свойства действия" };
            properties.Click += (_, _) => { if (selectedId is { } id) document?.Session.InspectAction(id); }; menu.Items.Add(properties);
            return menu;
        }
        list.ContextMenu = CreateMenu();
        ContextMenu = CreateMenu();
        var add = new Button { Name = "ScriptEventMenu", Content = "Добавить…", HorizontalAlignment = HorizontalAlignment.Left };
        add.ContextMenu = CreateMenu();
        add.Click += (_, _) => add.ContextMenu.Open(add);
        top.Children.Add(add);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(error, Dock.Bottom); root.Children.Add(error);
        var grid = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => new Border
        {
            Padding = new Thickness(8), Margin = new Thickness(0, 2), Background = new SolidColorBrush(Color.Parse("#292D33")),
            Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = row?.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = row?.Detail, FontSize = 12, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap } } }
        });
        details.Content = form;
        if (propertiesOnly) { list.MaxHeight = 130; DockPanel.SetDock(list, Dock.Top); grid.Children.Add(list); grid.Children.Add(details); }
        else grid.Children.Add(list);
        root.Children.Add(grid);
        if (actionOnly) { grid.Children.Remove(details); Content = details; }
        else Content = root;
        anchors.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            if (anchors.SelectedItem is Anchor { LineId: { } id }) document?.SelectLine(id);
            RefreshRows();
        };
        list.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            selectedId = (list.SelectedItem as Row)?.Action.Id; details.Offset = default;
            if (selectedId is { } id) document?.Session.InspectAction(id);
            BuildForm();
        };
        list.PointerReleased += (_, _) => { if (selectedId is { } id) document?.Session.InspectAction(id); };
        DataContextChanged += (_, _) =>
        {
            if (document is not null) document.PropertyChanged -= Changed;
            document = DataContext as SceneDocumentViewModel;
            if (document is not null) document.PropertyChanged += Changed;
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => { if (document is not null) document.PropertyChanged -= Changed; };
        AttachedToVisualTree += (_, _) => { if (document is not null) { document.PropertyChanged -= Changed; document.PropertyChanged += Changed; } Refresh(); };
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneDocumentViewModel.SelectedLine) && !refreshing && document?.SelectedLine is { } line)
        {
            refreshing = true;
            if (anchors.ItemsSource is IEnumerable<Anchor> items) anchors.SelectedItem = items.FirstOrDefault(a => a.LineId == line.Id);
            refreshing = false;
            RefreshRows();
        }
        else if (e.PropertyName is nameof(SceneDocumentViewModel.IsModified) or nameof(SceneDocumentViewModel.EditingLanguage)) Refresh();
    }

    private IEnumerable<Anchor> AnchorOptions()
    {
        yield return new(null, false, "Начало сцены");
        if (Scene is { } scene)
            for (var i = 0; i < scene.Lines.Length; i++)
            { var line = scene.Lines[i]; yield return new(line.Id, false, $"{i + 1}. {Speaker(line)}{Excerpt(line)}"); }
        yield return new(null, true, "Конец сцены");
    }

    private void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        var old = anchors.SelectedItem as Anchor;
        var options = AnchorOptions().ToArray(); anchors.ItemsSource = options;
        if (PropertiesOnly && Scene?.ScriptBlocks.FirstOrDefault(b => b.Action.Id == document?.Session.InspectedActionId) is { } inspected)
        { selectedId = inspected.Action.Id; old = options.FirstOrDefault(a => a.LineId == inspected.LineId && (a.LineId is not null || a.End == (inspected.Phase == ScriptPhase.SceneEnd))); }
        anchors.SelectedItem = old is { } && PropertiesOnly && selectedId == document?.Session.InspectedActionId ? old
            : old is { LineId: null } ? options.First(a => a.End == old.End && a.LineId is null)
            : options.FirstOrDefault(a => a.LineId == document?.SelectedLine?.Id) ?? options[0];
        refreshing = false; RefreshRows();
    }

    private void RefreshRows()
    {
        refreshing = true;
        var anchor = anchors.SelectedItem as Anchor;
        var scene = Scene;
        phases.ItemsSource = anchor?.LineId is not null ? LinePhases : new[] { new Phase(anchor?.End == true ? ScriptPhase.SceneEnd : ScriptPhase.SceneStart, anchor?.End == true ? "Конец сцены" : "Начало сцены") };
        phases.SelectedIndex = anchor?.LineId is not null ? 1 : 0;
        context.Text = scene?.HasLegacyActionScript == true
            ? "Импортированный сценарий: сохранён исходный порядок. Карточки показывают текст и смысл команд; новые сцены используют привязки к фразам."
            : anchor?.LineId is not null ? "Говорящий и размещение → «До» → текст → блоки «Во время» по очереди → ожидание продолжения → «После». Ожидание в блоке задерживает следующие блоки; запущенные движения продолжаются параллельно."
            : anchor?.End == true ? "Блоки после последней фразы." : "Блоки перед первой фразой.";
        ToolTip.SetTip(anchors, context.Text);
        Row[] rows = scene is null ? [] : scene.HasLegacyActionScript
            ? scene.Actions.Select(a => new Row(a, null, ActionLabel(a), Summary(a))).ToArray()
            : scene.ScriptBlocks.Where(b => b.LineId == anchor?.LineId && (b.LineId is not null || b.Phase == (anchor?.End == true ? ScriptPhase.SceneEnd : ScriptPhase.SceneStart)))
                .OrderBy(b => b.Phase).Select(b => new Row(b.Action, b, PhaseLabel(b.Phase) + " · " + ActionLabel(b.Action), Summary(b.Action))).ToArray();
        list.ItemsSource = rows; list.SelectedItem = rows.FirstOrDefault(r => r.Action.Id == selectedId) ?? rows.FirstOrDefault();
        selectedId = (list.SelectedItem as Row)?.Action.Id;
        refreshing = false; BuildForm();
    }

    private void BuildForm()
    {
        if (!PropertiesOnly)
        {
            return;
        }
        form.Children.Clear(); error.Text = ""; readDraft = null;
        if (list.SelectedItem is not Row row)
        { form.Children.Add(new HelpTip { Explanation = "Текст и порядок фраз уже работают. Добавьте поведение блоком или подключите Lua." }); return; }
        form.Children.Add(new TextBlock { Text = row.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        ComboBox? binding = null; ComboBox? timing = null;
        if (row.Block is { } block)
        {
            var bindingPanel = new StackPanel { Spacing = 5 };
            var options = AnchorOptions().ToArray();
            binding = new ComboBox { Name = "BlockAnchorPicker", ItemsSource = options, SelectedItem = options.First(a => a.LineId == block.LineId && (a.LineId is not null || a.End == (block.Phase == ScriptPhase.SceneEnd))), HorizontalAlignment = HorizontalAlignment.Stretch };
            bindingPanel.Children.Add(binding);
            timing = new ComboBox { ItemsSource = LinePhases, SelectedItem = LinePhases.FirstOrDefault(p => p.Value == block.Phase) ?? LinePhases[1] };
            bindingPanel.Children.Add(timing);
            form.Children.Add(new Expander { Header = "Изменить привязку", Content = bindingPanel });
        }
        var node = JsonSerializer.SerializeToNode<ScenarioAction>(row.Action)!;
        readDraft = Editor(node, form, "", row.Action);
        if (row.Action is RunScriptAction call)
        {
            var open = new Button { Name = "OpenLinkedLuaExternally", Content = "Открыть код" };
            open.Click += async (_, _) => { if (document is not null) await document.Session.OpenScriptExternallyAsync(call.ScriptId); };
            form.Children.Add(open);
        }
        var apply = new Button { Name = "ApplyScriptBlock", Content = "Применить" };
        apply.Click += (_, _) =>
        {
            try
            {
                if (Scene is not { } scene || document is null) return;
                var updated = readDraft!()!.Deserialize<ScenarioAction>()!;
                NovelScene candidate;
                if (row.Block is { } original)
                {
                    var target = (Anchor)binding!.SelectedItem!;
                    var phase = target.LineId is null ? target.End ? ScriptPhase.SceneEnd : ScriptPhase.SceneStart : ((Phase)timing!.SelectedItem!).Value;
                    candidate = scene with { ScriptBlocks = scene.ScriptBlocks.Replace(original, new(target.LineId, phase, updated)) };
                }
                else candidate = scene with { Actions = scene.Actions.Replace(row.Action, updated) };
                var project = document.Session.Project!;
                ScenarioProgram.Validate(project with { Scenes = project.Scenes.Replace(scene, candidate) });
                document.Session.UpdateScene(candidate);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
            { error.Text = ex.Message; }
        };
        form.Children.Add(apply);
    }

    private Func<JsonNode?> Editor(JsonNode? value, StackPanel parent, string name, ScenarioAction action)
    {
        if (name is "Id" or "kind" or "IsValid") return () => value?.DeepClone();
        if (name == "References") return () => value?.DeepClone();
        if (name == "Source" && action is ScriptAction inline)
        {
            parent.Children.Add(new HelpTip { Name = "LuaQueueHelp", Explanation = "Код редактируется во внешнем приложении. Открытие перенесёт этот блок в самостоятельный ресурс с сохранением ссылок." });
            var open = new Button { Name = "OpenInlineLuaExternally", Content = "Открыть код во внешнем редакторе" };
            open.Click += async (_, _) => { if (Scene is { } scene && document is not null) await document.Session.OpenInlineScriptExternallyAsync(scene.Id, inline.Id); };
            parent.Children.Add(open);
            return () => value?.DeepClone();
        }
        if (name.Length > 0)
        {
            if (value is JsonObject or JsonArray) parent.Children.Add(new TextBlock { Text = Label(name), FontWeight = FontWeight.SemiBold });
            else
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("100,6,*") };
                row.Children.Add(new TextBlock { Text = Label(name), TextWrapping = TextWrapping.Wrap, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var values = new StackPanel { Spacing = 4 }; Grid.SetColumn(values, 2); row.Children.Add(values); parent.Children.Add(row); parent = values;
            }
        }
        if (name == "Placement")
        {
            var automatic = new CheckBox { Content = "Автоматическое размещение", IsChecked = value is null }; parent.Children.Add(automatic);
            var read = Editor(value ?? JsonSerializer.SerializeToNode(new CharacterPlacement(0.5, 1, 1, 0)), parent, "", action);
            return () => automatic.IsChecked == true ? null : read();
        }
        if (value is JsonObject obj)
        {
            var actionField = obj.ContainsKey("TargetActionId") ? "TargetActionId" : obj.ContainsKey("ActionId") ? "ActionId" : null;
            var sceneField = actionField == "TargetActionId" ? "TargetSceneId" : "SceneId";
            var fields = obj.Where(p => actionField is null || p.Key != sceneField).Select(p => (p.Key, Read: Editor(p.Value, parent, p.Key, action))).ToArray();
            return () =>
            {
                var result = new JsonObject(); foreach (var f in fields) result[f.Key] = f.Read();
                if (actionField is not null)
                {
                    var target = Guid.TryParse(result[actionField]?.ToString(), out var id) ? id : Guid.Empty;
                    var owner = document?.Session.Project?.Scenes.FirstOrDefault(s => ScenarioProgram.Compile(s).Any(a => a.Id == target));
                    result[sceneField] = owner is null ? obj[sceneField]?.DeepClone() : JsonValue.Create(owner.Id);
                }
                return result;
            };
        }
        if (value is JsonArray array)
        {
            var container = new StackPanel { Spacing = 8 }; parent.Children.Add(container); var readers = new List<Func<JsonNode?>>();
            void Append(JsonNode? item)
            {
                var panel = new StackPanel { Spacing = 4, Margin = new Thickness(8, 0, 0, 4) };
                var read = Editor(item, panel, "", action); readers.Add(read);
                var remove = new Button { Content = "Убрать вариант" }; panel.Children.Add(remove);
                remove.Click += (_, _) => { readers.Remove(read); container.Children.Remove(panel); }; container.Children.Add(panel);
            }
            foreach (var item in array) Append(item);
            var add = new Button { Content = "Добавить вариант" }; parent.Children.Add(add);
            add.Click += (_, _) => { if (Scene?.Lines.FirstOrDefault() is { } line) Append(JsonSerializer.SerializeToNode(new ChoiceOption(Guid.NewGuid(), "Продолжить", Scene.Id, ScenarioProgram.LineEntryId(line.Id)))); };
            return () => new JsonArray(readers.Select(r => r()).ToArray());
        }
        if (name.EndsWith("Id", StringComparison.Ordinal) || name == "Token")
        {
            var refs = References(name, action).ToList(); var current = Guid.TryParse(value?.ToString(), out var id) ? id : (Guid?)null;
            if (!refs.Any(r => r.Id == current)) refs.Add(new(current, "Ссылка отсутствует — выберите объект"));
            var picker = new ComboBox { ItemsSource = refs, SelectedItem = refs.FirstOrDefault(r => r.Id == current), HorizontalAlignment = HorizontalAlignment.Stretch };
            parent.Children.Add(picker); return () => (picker.SelectedItem as Reference)?.Id is { } selected ? JsonValue.Create(selected) : null;
        }
        if (name == "Mode")
        {
            var input = new ComboBox { ItemsSource = WaitModes, SelectedItem = WaitModes.FirstOrDefault(m => m.Value == value?.ToString()) }; parent.Children.Add(input);
            return () => JsonValue.Create((input.SelectedItem as WaitMode)?.Value);
        }
        if (value is JsonValue j && j.GetValueKind() == JsonValueKind.Number)
        {
            var percent = name is "X" or "Y" or "Width" or "Height" or "Scale";
            var input = new NumericUpDown { Value = (decimal)j.GetValue<double>() * (percent ? 100 : 1), Minimum = -1000, Maximum = 86400, Increment = percent || name == "Layer" ? 1 : 0.1m };
            parent.Children.Add(input); return () => name == "Layer" ? JsonValue.Create((int)(input.Value ?? 0)) : JsonValue.Create((input.Value ?? 0) / (percent ? 100 : 1));
        }
        if (value is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        { var input = new CheckBox { IsChecked = boolean.GetValue<bool>() }; parent.Children.Add(input); return () => JsonValue.Create(input.IsChecked == true); }
        var text = new TextBox { Text = value?.ToString() ?? "", MinHeight = 32, TextWrapping = TextWrapping.Wrap };
        parent.Children.Add(text);
        return () => JsonValue.Create(text.Text ?? "");
    }

    private IEnumerable<Reference> References(string field, ScenarioAction action)
    {
        if (document?.Session.Project is not { } project) yield break;
        if (field == "Token") yield return new(null, "Любой активный предмет");
        if (field == "ScriptId") foreach (var s in project.Scripts) yield return new(s.Id, "Lua: " + s.Name);
        if (field is "ObjectId" or "Lua") foreach (var c in project.Characters) yield return new(c.Id, c.Name);
        if (field is "StateId" or "Lua") foreach (var c in project.Characters) foreach (var s in c.States) yield return new(s.Id, c.Name + " / " + s.Name);
        foreach (var scene in project.Scenes)
        {
            if (field is "SceneId" or "TargetSceneId" or "Lua") yield return new(scene.Id, scene.Name);
            foreach (var line in scene.Lines)
            {
                if (field == "Lua" || field == "LineId" && scene.Id == Scene?.Id) yield return new(line.Id, scene.Name + " · " + Excerpt(line));
                if (!scene.HasLegacyActionScript && field is "ActionId" or "TargetActionId") yield return new(ScenarioProgram.LineEntryId(line.Id), scene.Name + " · " + Excerpt(line));
            }
            foreach (var hotspot in scene.Actions.Concat(scene.ScriptBlocks.Select(b => b.Action)).OfType<HotspotAction>())
                if (field is "Token" or "Lua" || field == "ObjectId" && action is HotspotAction) yield return new(hotspot.ObjectId, hotspot.Label);
            if (scene.HasLegacyActionScript && field is "ActionId" or "TargetActionId")
                foreach (var a in scene.Actions) yield return new(a.Id, scene.Name + " · " + ActionLabel(a) + " · " + Summary(a));
        }
        if (field == "ObjectId" && action is HotspotAction own) yield return new(own.ObjectId, own.Label.Length > 0 ? own.Label : "Новый предмет");
        if (field == "Lua") foreach (var s in project.Scripts) yield return new(s.Id, "Lua: " + s.Name);
    }

    private void Add(int kind)
    {
        if (Scene is not { } scene || document?.Session.Project is not { } project || !document.Session.CanEdit) return;
        if (scene.HasLegacyActionScript) { error.Text = "Новые привязки доступны в сценах с последовательностью фраз. Импортированный поток остаётся доступен для правки существующих карточек."; return; }
        var id = Guid.NewGuid(); var actor = project.Characters.FirstOrDefault(); var line = scene.Lines.FirstOrDefault();
        if (kind is >= 0 and <= 4 && actor is null) { error.Text = "Сначала создайте персонажа в библиотеке."; return; }
        if (kind is 6 or 7 or 9 && line is null) { error.Text = "Сначала добавьте фразу для перехода."; return; }
        var target = line is null ? Guid.Empty : ScenarioProgram.LineEntryId(line.Id);
        if (kind == 11)
        {
            var chosenAnchor = (Anchor)anchors.SelectedItem!;
            var chosenPhase = ((Phase)phases.SelectedItem!).Value;
            var owner = document.Session; var sceneId = scene.Id;
            _ = owner.CreatePhraseScriptAsync(sceneId, chosenAnchor.LineId, chosenPhase);
            return;
        }
        if (kind == 12 && project.Scripts.IsEmpty) { error.Text = "Сначала создайте скрипт через «Ресурсы → Lua-скрипты»."; return; }
        if (kind == 12)
        {
            var chosenAnchor = (Anchor)anchors.SelectedItem!;
            _ = document.Session.AttachExistingScriptAsync(scene.Id, chosenAnchor.LineId, ((Phase)phases.SelectedItem!).Value);
            return;
        }
        ScenarioAction action = kind switch
        {
            0 => new MoveAction(id, actor?.Id ?? Guid.Empty, new(0.75, 1, 1, 0), 1),
            1 => new ShowAction(id, actor?.Id ?? Guid.Empty), 2 => new HideAction(id, actor?.Id ?? Guid.Empty),
            3 => new StateAction(id, actor?.Id ?? Guid.Empty, actor?.DefaultStateId ?? Guid.Empty),
            4 => new PlaceAction(id, actor?.Id ?? Guid.Empty, new(0.5, 1, 1, 0)), 5 => new WaitAction(id, "timer", 1),
            6 => new ChoiceAction(id, [new(Guid.NewGuid(), "Продолжить", scene.Id, target)]), 7 => new JumpAction(id, scene.Id, target),
            8 => new SetAction(id, "flag", "true"), 9 => new BranchAction(id, "flag", "true", scene.Id, target),
            10 => new HotspotAction(id, Guid.NewGuid(), "Предмет", 0.4, 0.4, 0.2, 0.1),
            12 => new RunScriptAction(id, project.Scripts[0].Id),
            _ => new ScriptAction(id, "-- Поведение выбранной фразы\n-- game.move(refs.actor, 0.75, 1, 1, 0, 2)", "phrase.lua")
        };
        var anchor = (Anchor)anchors.SelectedItem!; var phase = ((Phase)phases.SelectedItem!).Value;
        selectedId = id;
        document.Session.UpdateScene(scene with { ScriptBlocks = scene.ScriptBlocks.Add(new(anchor.LineId, phase, action)) });
        document.Session.InspectAction(id);
    }

    private void Move(int offset)
    {
        if (Scene is not { } scene || document is null || list.SelectedItem is not Row row) return;
        if (row.Block is { } block)
        {
            var peers = scene.ScriptBlocks.Where(b => b.LineId == block.LineId && b.Phase == block.Phase).ToArray();
            var index = Array.IndexOf(peers, block); if (index + offset < 0 || index + offset >= peers.Length) return;
            var from = scene.ScriptBlocks.IndexOf(block); var to = scene.ScriptBlocks.IndexOf(peers[index + offset]);
            document.Session.UpdateScene(scene with { ScriptBlocks = scene.ScriptBlocks.RemoveAt(from).Insert(to, block) });
        }
        else
        {
            var index = scene.Actions.IndexOf(row.Action); if (index + offset < 0 || index + offset >= scene.Actions.Length) return;
            document.Session.UpdateScene(scene with { Actions = scene.Actions.RemoveAt(index).Insert(index + offset, row.Action) });
        }
    }

    private string Excerpt(DialogueLine line)
    { var text = line.Texts.GetValueOrDefault(document?.EditingLanguage ?? "ru", "Пустая фраза").Replace('\n', ' '); return text.Length > 90 ? text[..90] + "…" : text; }
    private string Speaker(DialogueLine line) => line.SpeakerId is { } id ? Friendly(id) + ": " : "";
    private string Friendly(Guid id)
    {
        var project = document?.Session.Project; if (project is null) return "Не найдено";
        foreach (var s in project.Scripts) if (s.Id == id) return s.Name;
        foreach (var c in project.Characters) { if (c.Id == id) return c.Name; foreach (var s in c.States) if (s.Id == id) return c.Name + " / " + s.Name; }
        foreach (var s in project.Scenes) { if (s.Id == id) return s.Name; foreach (var l in s.Lines) if (l.Id == id || ScenarioProgram.LineEntryId(l.Id) == id) return Excerpt(l);
            foreach (var h in s.Actions.Concat(s.ScriptBlocks.Select(b => b.Action)).OfType<HotspotAction>()) if (h.ObjectId == id) return h.Label; }
        return "Ссылка отсутствует";
    }
    private string Summary(ScenarioAction a) => a switch
    {
        MoveAction m => $"{Friendly(m.ObjectId)} → X {m.Target.X * 100:0.#}%, Y {m.Target.Y * 100:0.#}% за {m.Seconds:0.#} с",
        PlaceAction p => Friendly(p.ObjectId) + (p.Placement is { } v ? $" · X {v.X * 100:0.#}%, Y {v.Y * 100:0.#}%" : " · автоматически"),
        ShowAction s => Friendly(s.ObjectId), HideAction h => Friendly(h.ObjectId), StateAction s => Friendly(s.StateId),
        TextAction t => Friendly(t.LineId), WaitAction w => WaitModes.FirstOrDefault(m => m.Value == w.Mode)?.Label + (w.Mode == "timer" ? $" · {w.Seconds:0.#} с" : ""),
        JumpAction j => "К фразе: " + Friendly(j.ActionId), BranchAction b => $"Если {b.Variable} = {b.EqualsValue} → {Friendly(b.ActionId)}",
        ChoiceAction c => string.Join(" / ", c.Options.Select(o => o.Text)), SetAction s => s.Variable + " = " + s.Value,
        ScriptAction s => s.Source.Split('\n').FirstOrDefault(l => !l.TrimStart().StartsWith("--") && l.Trim().Length > 0) ?? "Свой код Lua",
        RunScriptAction s => Friendly(s.ScriptId),
        HotspotAction h => h.Label, _ => "Команда сценария"
    };
    private static string PhaseLabel(ScriptPhase phase) => phase switch { ScriptPhase.BeforeLine => "До фразы", ScriptPhase.DuringLine => "Во время фразы", ScriptPhase.AfterLine => "После продолжения", ScriptPhase.SceneEnd => "Конец сцены", _ => "Начало сцены" };
    private static string ActionLabel(ScenarioAction action) => action switch { TextAction => "Показать текст", ShowAction => "Показать", HideAction => "Скрыть", StateAction => "Сменить состояние", PlaceAction => "Разместить", MoveAction => "Двигать", WaitAction => "Ожидать", ChoiceAction => "Выбор", JumpAction => "Переход", SetAction => "Переменная", BranchAction => "Условие", ScriptAction => "Lua", RunScriptAction => "Вызвать Lua-скрипт", HotspotAction => "Предмет", _ => "Действие" };
    private static string Label(string name) => name switch
    {
        "LineId" => "Фраза", "ObjectId" => "Персонаж / предмет", "StateId" => "Состояние", "SceneId" or "TargetSceneId" => "Сцена", "ActionId" or "TargetActionId" => "Целевая фраза",
        "ScriptId" => "Ресурс Lua",
        "Seconds" => "Длительность, с", "Mode" => "Чего ожидать", "Source" => "Lua-код", "Token" => "Предмет", "Name" => "Имя скрипта для сообщений об ошибках", "Label" => "Подпись", "Text" => "Текст варианта",
        "Variable" => "Переменная", "Value" => "Значение", "EqualsValue" => "Равно", "Options" => "Варианты", "Target" => "Конечное положение", "Placement" => "Размещение",
        "X" => "X, % сцены", "Y" => "Y, % сцены", "Width" => "Ширина, % сцены", "Height" => "Высота, % сцены", "Scale" => "Масштаб, %", "Layer" => "Порядок спрайта", "Enabled" => "Активен", _ => name
    };
}

