using System.Collections.Immutable;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NovellMaker.Core.Screens;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

public sealed class ScreenPropertiesView : UserControl
{
    private sealed record Option(Guid? Id, string Label) { public override string ToString() => Label; }
    private EditorSession? session;
    private bool refreshing;
    private sealed record BindingOption(DialogueBinding Value, string Label) { public override string ToString() => Label; }
    private readonly ComboBox dialogueBinding = new() { Name = "DialogueBinding", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown dialogueFontSize = Number("DialogueFontSize", 8, 160);
    private readonly TextBox dialogueTextColor = new() { Name = "DialogueTextColor" };
    private readonly TextBox dialogueBackground = new() { Name = "DialogueBackground" };
    private readonly TextBox dialogueBorder = new() { Name = "DialogueBorderColor" };
    private readonly NumericUpDown dialogueBorderWidth = Number("DialogueBorderWidth", 0, 8192);
    private readonly NumericUpDown dialogueRadius = Number("DialogueRadius", 0, 8192);
    private readonly StackPanel dialogueFields = new() { Spacing = 6 };
    private readonly TextBox name = new() { Name = "ScreenNodeName", IsUndoEnabled = false };
    private readonly TextBox text = new() { Name = "ScreenNodeText", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65, IsUndoEnabled = false };
    private readonly NumericUpDown[] geometry = [Number("ScreenNodeX", -100000, 100000), Number("ScreenNodeY", -100000, 100000), Number("ScreenNodeWidth", 1, 100000), Number("ScreenNodeHeight", 1, 100000)];
    private readonly CheckBox visible = new() { Name = "ScreenNodeVisible", Content = "Видим" };
    private readonly CheckBox enabled = new() { Name = "ScreenNodeEnabled", Content = "Доступен" };
    private readonly ComboBox parent = new() { Name = "ScreenNodeParent", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown[] transform = [Number("ScreenAnchorX", 0, 1), Number("ScreenAnchorY", 0, 1), Number("ScreenPivotX", 0, 1), Number("ScreenPivotY", 0, 1), Number("ScreenScaleX", -1000, 1000), Number("ScreenScaleY", -1000, 1000), Number("ScreenRotation", -360000, 360000), Number("ScreenOpacity", 0, 1)];
    private readonly NumericUpDown orderValue = Number("ScreenNodeOrder", -100000, 100000);
    private readonly TextBox color = new() { Name = "ScreenNodeColor", IsUndoEnabled = false };
    private readonly CheckBox clipping = new() { Name = "ScreenNodeClip", Content = "Обрезать потомков" };
    private readonly ComboBox sprite = new() { Name = "ScreenNodeSprite", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProjectImageView spritePreview = new() { Name = "ScreenSpritePreview", Height = 80, MaximumWidth = 400, TransparentBackground = true };
    private readonly ComboBox action = new() { Name = "ScreenButtonAction", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Lua-обработчик", "Закрыть", "Открыть поверх", "Заменить слой", "Назад", "Не назначено", "Перейти к сцене", "Громкость группы", "Mute группы", "Продолжить реплику", "Быстро сохранить", "Быстро загрузить", "Переключить слой", "Новая игра", "В главное меню", "Завершить сеанс", "Продолжить игру · последний слот", "Запросить список слотов", "Сохранить в слот", "Загрузить слот", "Удалить слот" } };
    private readonly TextBox slotId = new() { Name = "ScreenSlotId", IsUndoEnabled = false, PlaceholderText = "Например: chapter-1" };
    private readonly TextBox slotTitle = new() { Name = "ScreenSlotTitle", IsUndoEnabled = false, MaxLength = 128 };
    private readonly StackPanel slotFields = new() { Spacing = 6 };
    private readonly Control slotTitleField;
    private readonly ComboBox target = new() { Name = "ScreenButtonTarget", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox screenName = new() { Name = "ScreenName", IsUndoEnabled = false };
    private readonly ComboBox handler = new() { Name = "ScreenHandler", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox shortcut = new() { Name = "ProjectKey", PlaceholderText = "Например: P или F9" };
    private readonly ComboBox shortcutAction = new() { Name = "ProjectKeyAction", ItemsSource = new[] { "Переключить слой", "Открыть слой", "Перейти к сцене", "Назад", "Продолжить реплику", "Быстро сохранить", "Быстро загрузить", "Новая игра", "В главное меню", "Завершить сеанс", "Продолжить игру · последний слот", "Запросить список слотов" }, SelectedIndex = 0 };
    private static readonly ScreenButtonAction[] shortcutActions = [ScreenButtonAction.Toggle, ScreenButtonAction.Open, ScreenButtonAction.Scene, ScreenButtonAction.Back, ScreenButtonAction.Continue, ScreenButtonAction.Save, ScreenButtonAction.Load, ScreenButtonAction.NewGame, ScreenButtonAction.MainMenu, ScreenButtonAction.EndSession, ScreenButtonAction.ContinueGame, ScreenButtonAction.ListSlots];
    private readonly TextBlock shortcuts = new() { Name = "ProjectKeyBindings", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox audioGroup = new() { Name = "ScreenAudioGroup", ItemsSource = Enum.GetValues<NovellMaker.Core.Audio.AudioGroup>(), SelectedIndex = 0 };
    private readonly NumericUpDown audioAmount = Number("ScreenAudioAmount", -100, 100);
    private readonly StackPanel audioFields = new() { Spacing = 6 };
    private readonly CheckBox startScreen = new() { Name = "ProjectStartScreen", Content = "Стартовый экран проекта (без запуска сюжета)" };
    private readonly CheckBox pauseBelow = new() { Name = "ScenePauseOnOverlay", Content = "Приостановить сцену под этим слоем" };
    private readonly CheckBox blockBelow = new() { Name = "SceneBlockInputOnOverlay", Content = "Блокировать ввод нижних слоёв" };
    private readonly TextBox alias = new() { Name = "ScreenReferenceName", PlaceholderText = "Имя в Lua" };
    private readonly TextBlock refs = new() { Name = "ScreenReferences", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock status = new() { Name = "ScreenEditorStatus", Foreground = Brush.Parse("#E2B684"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold };
    private readonly StackPanel body = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly Control textField;
    private readonly Control targetField;
    private readonly StackPanel buttonFields = new() { Spacing = 6 };
    public ScreenPropertiesView()
    {
        body.Children.Add(title); Field(body, "Название", name); textField = Field(body, "Текст", text);
        Field(body, "Источник диалога", dialogueBinding, "Текст и имя берутся из текущей фразы. Их содержание редактируется во вкладке «Фразы». Привязка не выполняет сценарий.");
        Button(dialogueFields, "MaterializeDialogue", "Размещать компонентами", () => session?.UseCanvasDialogueLayout());
        Field(dialogueFields, "Размер шрифта", dialogueFontSize); Field(dialogueFields, "Цвет текста", dialogueTextColor);
        Field(dialogueFields, "Фон", dialogueBackground); Field(dialogueFields, "Цвет границы", dialogueBorder);
        Field(dialogueFields, "Толщина границы", dialogueBorderWidth); Field(dialogueFields, "Радиус углов", dialogueRadius);
        dialogueFields.Children.Add(new HelpTip { Explanation = "Оформление общее для компонентов диалога сцены. Изменение создаёт собственный CSS сцены; шрифт, выравнивание, отступы и анимация также доступны на вкладке оформления. CSS-панель наследует размер и положение; ручное размещение фиксирует текущую компоновку." });
        body.Children.Add(new Expander { Name = "DialogueStyleInspector", Header = "Оформление диалога", IsExpanded = true, Content = dialogueFields });
        var grid = new Grid { ColumnDefinitions = new("*,8,*"), RowDefinitions = new("Auto,Auto"), RowSpacing = 6 };
        var labels = new[] { "X", "Y", "Ширина", "Высота" };
        for (var i = 0; i < 4; i++) { var cell = new StackPanel { Spacing = 3 }; Field(cell, labels[i], geometry[i]); Grid.SetRow(cell, i / 2); Grid.SetColumn(cell, i % 2 * 2); grid.Children.Add(cell); }
        body.Children.Add(grid);
        Field(body, "Родитель", parent, "Локальные координаты сохраняются. Потомки и корень не могут стать родителями выбранного узла.");
        var transforms = new StackPanel { Spacing = 5 };
        var transformLabels = new[] { "Anchor X", "Anchor Y", "Pivot X", "Pivot Y", "Масштаб X", "Масштаб Y", "Угол, °", "Непрозрачность" };
        for (var i = 0; i < transform.Length; i++) { transform[i].Increment = i == 6 ? 1 : .05m; Field(transforms, transformLabels[i], transform[i]); }
        Field(transforms, "Цвет #RRGGBB", color); Field(transforms, "Порядок среди соседей", orderValue); transforms.Children.Add(clipping);
        body.Children.Add(new Expander { Name = "ScreenGeometryExpander", Header = "Преобразование и clipping", Content = transforms, HorizontalAlignment = HorizontalAlignment.Stretch });
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; flags.Children.Add(visible); flags.Children.Add(enabled); body.Children.Add(flags);
        Field(body, "Спрайт", sprite, "Изображение из файлов проекта. Его можно выбрать, импортировать или перетащить сюда. Ссылка сохраняется при переименовании файла.");
        var spriteArea = new Border { Name = "ScreenSpriteDrop", MinHeight = 64, Background = Brush.Parse("#20252D"), BorderBrush = Brush.Parse("#465567"), BorderThickness = new Thickness(1), Child = spritePreview };
        DragDrop.SetAllowDrop(spriteArea, true);
        spriteArea.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = SpriteDrop(e) is null ? DragDropEffects.None : DragDropEffects.Copy; e.Handled = true; });
        spriteArea.AddHandler(DragDrop.DropEvent, (_, e) => { if (SpriteDrop(e) is { } id) Edit(n => n with { ImageId = id }); e.Handled = true; }); body.Children.Add(spriteArea);
        var spriteTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Async(spriteTools, "ImportScreenSprite", "Из файла…", () => session?.ImportScreenSpriteAsync() ?? Task.CompletedTask);
        Button(spriteTools, "ClearScreenSprite", "Убрать", () => Edit(n => n with { ImageId = null })); body.Children.Add(spriteTools);
        Field(audioFields, "Аудиогруппа", audioGroup); Field(audioFields, "Изменение громкости, %", audioAmount); buttonFields.Children.Add(audioFields);
        Field(buttonFields, "Нажатие", action); targetField = Field(buttonFields, "Цель", target); body.Children.Add(buttonFields);
        Field(slotFields, "ID слота", slotId, "1–64 символа a–z, 0–9, '_' или '-'. Пустое поле при сохранении создаёт новый ID. quicksave — быстрый слот.");
        slotTitleField = Field(slotFields, "Название сохранения", slotTitle, "Необязательно. По умолчанию — название текущей сцены.");
        buttonFields.Children.Add(slotFields);
        var order = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        Button(order, "MoveScreenNodeUp", "Ниже", () => session?.MoveScreenNode(-1)); Button(order, "MoveScreenNodeDown", "Выше", () => session?.MoveScreenNode(1));
        Button(order, "CopyScreenNode", "Копия", () => Try(() => session?.DuplicateScreenNode()));
        Async(order, "DeleteScreenNode", "Удалить", () => session?.SelectedScreenNode is { } n ? session.DeleteScreenNodeAsync(n.Id) : Task.CompletedTask); body.Children.Add(order);
        var links = new StackPanel { Spacing = 6 }; Field(links, "Имя в Lua", alias, "Ссылка на выбранный элемент. В коде используется refs.имя; название элемента меняется независимо.");
        Button(links, "AddScreenReference", "Привязать", () => { if (session?.SelectedScreen is { } s && session.SelectedScreenNode is { } n) Try(() => session.UpdateScreen(s with { References = s.References.SetItem(alias.Text ?? "", n.Id) })); });
        Button(links, "RemoveScreenReference", "Удалить ссылку", () => { if (session?.SelectedScreen is { } s) session.UpdateScreen(s with { References = s.References.Remove(alias.Text ?? "") }); }); links.Children.Add(refs);
        body.Children.Add(new Expander { Name = "ScreenReferencesExpander", Header = "Ссылки Lua", Content = links, HorizontalAlignment = HorizontalAlignment.Stretch });
        var settings = new StackPanel { Spacing = 7 }; Field(settings, "Название сцены", screenName);
        settings.Children.Add(startScreen);
        Button(settings, "AddGameMenusExample", "Добавить пример главного меню и слотов", () => session?.AddGameMenusExample());
        Button(settings, "AddReleaseCheckExample", "Добавить пример проверки выпуска (F5)", () => session?.AddReleaseCheckExample());
        Button(settings, "AddPlayerSettingsExample", "Добавить пример настроек игрока (F9)", () => session?.AddPlayerSettingsExample());
        Button(settings, "AddTriggersExample", "Добавить пример триггеров и данных (F8)", () => session?.AddTriggersExample());
        Button(settings, "AddComponentStateExample", "Добавить пример состояния компонентов (F7)", () => session?.AddComponentStateExample());
        Button(settings, "AddLuaModulesExample", "Добавить пример модулей Lua (F4)", () => session?.AddLuaModulesExample());
        Button(settings, "AddLuaLibraryExample", "Добавить пример библиотеки Lua (F3)", () => session?.AddLuaLibraryExample());
        Button(settings, "AddLuaRandomExample", "Добавить пример управляемого RNG (F2)", () => session?.AddLuaRandomExample());
        Button(settings, "AddLuaDiagnosticsExample", "Добавить пример журнала Lua (F1)", () => session?.AddLuaDiagnosticsExample());
        Button(settings, "AddMinigameExample", "Добавить карточный пазл и запуск в сюжете", () => session?.AddMinigameExample());
        Button(settings, "AddAnimationsExample", "Добавить пример анимаций (F11)", () => session?.AddAnimationsExample());
        Button(settings, "AddAudioExample", "Добавить пример звука Lua (F10, импортированный WAV)", () => session?.AddAudioExample());
        Button(settings, "AddInputExample", "Добавить пример ввода (F12)", () => session?.AddInputExample());
        var minigameProperties = new MinigamePropertiesView();
        minigameProperties.Bind(DataContextProperty, new Avalonia.Data.Binding());
        settings.Children.Add(new Expander { Name = "MinigameResourceExpander", Header = "Мини-игра", Content = minigameProperties, HorizontalAlignment = HorizontalAlignment.Stretch });
        startScreen.IsCheckedChanged += (_, _) => { if (!refreshing && session?.SelectedScreen is { } selected) Try(() => session.SetStartScreen(startScreen.IsChecked == true ? selected.Id : null)); };
        settings.Children.Add(pauseBelow); settings.Children.Add(blockBelow);
        settings.Children.Add(new HelpTip { Explanation = "Эти настройки действуют при открытии поверх другой сцены. При обычном переходе сцена исполняет собственные фразы и скрипт. Слой показывает компоненты и принимает их события; нижний сюжет сохраняет свою позицию." });
        var keys = new StackPanel { Spacing = 6 };
        Field(keys, "Клавиша проекта", shortcut); Field(keys, "Команда", shortcutAction);
        Button(keys, "SetProjectKey", "Назначить", () => { if (session?.SelectedScreen is { } selected) Try(() =>
        {
            var kind = shortcutActions[Math.Max(0, shortcutAction.SelectedIndex)];
            session.SetInputBinding(new(shortcut.Text ?? "", kind, kind is ScreenButtonAction.Open or ScreenButtonAction.Toggle or ScreenButtonAction.Scene or ScreenButtonAction.NewGame ? selected.Id : null));
        }); });
        Button(keys, "RemoveProjectKey", "Удалить назначение", () => session?.RemoveInputBinding(shortcut.Text ?? ""));
        keys.Children.Add(shortcuts);
        settings.Children.Add(new Expander { Name = "ProjectKeysExpander", Header = "Клавиши проекта", Content = keys, HorizontalAlignment = HorizontalAlignment.Stretch });
        Field(settings, "Обработчик Lua", handler);
        var code = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        Async(code, "CreateScreenHandler", "Создать…", () => session?.CreateScreenHandlerAsync() ?? Task.CompletedTask);
        Async(code, "OpenScreenHandler", "Открыть код", () => session?.SelectedScreen?.HandlerScriptId is { } id ? session.OpenScriptExternallyAsync(id) : Task.CompletedTask); settings.Children.Add(code);
        body.Children.Add(new Expander { Name = "ScreenSettingsExpander", Header = "Сцена", IsExpanded = true, Content = settings, HorizontalAlignment = HorizontalAlignment.Stretch });
        body.Children.Add(status); Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Edit(n => n with { Name = name.Text ?? "" }, "name"); };
        dialogueBinding.SelectionChanged += (_, _) => { if (!refreshing && dialogueBinding.SelectedItem is BindingOption option) Edit(n => n with { Dialogue = option.Value }); };
        void Style(string property, string value) { if (!refreshing) Try(() => session?.SetDialogueStyleProperty(property, value)); }
        dialogueFontSize.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty && dialogueFontSize.Value is { } value) Style("font-size", value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px"); };
        dialogueBorderWidth.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty && dialogueBorderWidth.Value is { } value) Style("border-width", value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px"); };
        dialogueRadius.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty && dialogueRadius.Value is { } value) Style("border-radius", value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "px"); };
        dialogueTextColor.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Style("color", dialogueTextColor.Text ?? ""); };
        dialogueBackground.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Style("background-color", dialogueBackground.Text ?? ""); };
        dialogueBorder.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Style("border-color", dialogueBorder.Text ?? ""); };
        text.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Edit(n => n with { Text = text.Text ?? "" }, "text"); };
        foreach (var number in geometry) number.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty && !refreshing && geometry.All(n => n.Value is not null)) Edit(n => n with { Bounds = new((double)geometry[0].Value!.Value, (double)geometry[1].Value!.Value, (double)geometry[2].Value!.Value, (double)geometry[3].Value!.Value) }, number.Name); };
        visible.IsCheckedChanged += (_, _) => Edit(n => n with { Visible = visible.IsChecked == true });
        enabled.IsCheckedChanged += (_, _) => Edit(n => n with { Enabled = enabled.IsChecked == true });
        clipping.IsCheckedChanged += (_, _) => Edit(n => n with { ClipChildren = clipping.IsChecked == true });
        parent.SelectionChanged += (_, _) => { if (!refreshing && parent.SelectedItem is Option { Id: { } id }) Try(() => session?.ReparentScreenNode(id)); };
        foreach (var number in transform) number.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty && !refreshing && transform.All(n => n.Value is not null))
                Edit(n => n with { Geometry = new() { AnchorX = (double)transform[0].Value!.Value, AnchorY = (double)transform[1].Value!.Value,
                    PivotX = (double)transform[2].Value!.Value, PivotY = (double)transform[3].Value!.Value, ScaleX = (double)transform[4].Value!.Value,
                    ScaleY = (double)transform[5].Value!.Value, Rotation = (double)transform[6].Value!.Value, Opacity = (double)transform[7].Value!.Value } }, number.Name);
        };
        orderValue.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty) Edit(n => n with { Order = (int)(orderValue.Value ?? 0) }, "order"); };
        color.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && ScreenTint.TryParse(color.Text, out var ignoredTint)) Edit(n => n with { Color = color.Text! }, "color"); };
        sprite.SelectionChanged += (_, _) => { if (!refreshing) Edit(n => n with { ImageId = (sprite.SelectedItem as Option)?.Id }); };
        action.SelectionChanged += (_, _) =>
        {
            if (refreshing) return;
            var kind = (ScreenButtonAction)action.SelectedIndex;
            slotFields.IsVisible = kind is ScreenButtonAction.SaveSlot or ScreenButtonAction.LoadSlot or ScreenButtonAction.DeleteSlot;
            slotTitleField.IsVisible = kind == ScreenButtonAction.SaveSlot;
            ApplyButton();
        };
        target.SelectionChanged += (_, _) =>
        {
            if (!refreshing && (ScreenButtonAction)action.SelectedIndex is ScreenButtonAction.Open or ScreenButtonAction.Replace or
                ScreenButtonAction.Scene or ScreenButtonAction.Toggle or ScreenButtonAction.NewGame) ApplyButton();
        };
        screenName.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !refreshing && session?.SelectedScreen is { } s && !string.IsNullOrWhiteSpace(screenName.Text)) session.UpdateScreen(s with { Name = screenName.Text }, "screen-name:" + s.Id); };
        handler.SelectionChanged += (_, _) => { if (!refreshing && session?.SelectedScreen is { } s) session.UpdateScreen(s with { HandlerScriptId = (handler.SelectedItem as Option)?.Id }); };
        slotId.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !refreshing) ApplyButton(); };
        slotTitle.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !refreshing) ApplyButton(); };
        audioGroup.SelectionChanged += (_, _) => { if (!refreshing) ApplyButton(); };
        audioAmount.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty && !refreshing) ApplyButton(); };
        pauseBelow.IsCheckedChanged += (_, _) => { if (!refreshing && session?.SelectedScreen is { } s) session.UpdateScreen(s with { PauseOnOverlay = pauseBelow.IsChecked == true }); };
        blockBelow.IsCheckedChanged += (_, _) => { if (!refreshing && session?.SelectedScreen is { } s) session.UpdateScreen(s with { BlockInputOnOverlay = blockBelow.IsChecked == true }); };
        AddHandler(LostFocusEvent, (_, _) => session?.EndEditGroup(), Avalonia.Interactivity.RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); };
        AttachedToVisualTree += (_, _) => { Unsubscribe(); session = DataContext as EditorSession; Subscribe(); Refresh(); }; DetachedFromVisualTree += (_, _) => Unsubscribe();
    }
    private Guid? SpriteDrop(DragEventArgs e) => session?.CanEdit == true ? ComponentDrop.Decode(e.DataTransfer.TryGetText(), session)?.ImageId : null;
    private static NumericUpDown Number(string name, decimal min, decimal max) => new() { Name = name, Minimum = min, Maximum = max, Increment = 1, FormatString = "0.##" };
    private static Control Field(StackPanel panel, string label, Control control, string? explanation = null)
    {
        var field = new StackPanel { Spacing = 3 }; var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        if (explanation is not null) row.Children.Add(new HelpTip { Explanation = explanation }); field.Children.Add(row); field.Children.Add(control); panel.Children.Add(field); return field;
    }
    private static void Button(StackPanel panel, string name, string label, Action action) { var b = new Button { Name = name, Content = label }; b.Click += (_, _) => action(); panel.Children.Add(b); }
    private static void Async(StackPanel panel, string name, string label, Func<Task> action) { var b = new Button { Name = name, Content = label }; b.Click += async (_, _) => await action(); panel.Children.Add(b); }
    private void Try(Action action) { try { action(); status.Text = ""; } catch (Exception error) when (error is ArgumentException or NovellMaker.Core.Styling.DialogueCssException) { status.Text = error.Message; } }
    private void Edit(Func<ScreenNode, ScreenNode> update, string? field = null)
    {
        if (refreshing || session?.SelectedScreenNode is not { } node) return;
        Try(() => session.UpdateScreenNode(update(node), field is null ? null : $"screen:{node.Id}:{field}"));
    }
    private void ApplyButton()
    {
        if (session?.SelectedScreen is not { } screen || session.SelectedScreenNode is not { Kind: ScreenNodeKind.Button or ScreenNodeKind.Area } node) return;
        var kind = (ScreenButtonAction)Math.Max(0, action.SelectedIndex);
        var id = kind is ScreenButtonAction.Open or ScreenButtonAction.Replace or ScreenButtonAction.Scene or ScreenButtonAction.Toggle or ScreenButtonAction.NewGame ? (target.SelectedItem as Option)?.Id : null;
        if (kind is ScreenButtonAction.Open or ScreenButtonAction.Replace or ScreenButtonAction.Scene or ScreenButtonAction.Toggle or ScreenButtonAction.NewGame && id is null) { status.Text = "Выберите целевую сцену."; return; }
        var usesSlot = kind is ScreenButtonAction.SaveSlot or ScreenButtonAction.LoadSlot or ScreenButtonAction.DeleteSlot;
        var selectedSlot = usesSlot && !string.IsNullOrWhiteSpace(slotId.Text) ? slotId.Text : null;
        if (usesSlot && (selectedSlot is not null && !NovellMaker.Core.Execution.GameSlotStore.IsValidSlotId(selectedSlot) || kind != ScreenButtonAction.SaveSlot && selectedSlot is null))
        { status.Text = "Укажите допустимый ID слота."; return; }
        Try(() => session.UpdateScreen(screen with { Buttons = screen.Buttons.Where(b => b.NodeId != node.Id).Append(new(node.Id, kind, id) {
            SlotId = selectedSlot, SlotTitle = kind == ScreenButtonAction.SaveSlot && !string.IsNullOrEmpty(slotTitle.Text) ? slotTitle.Text : null,
            Group = audioGroup.SelectedItem is NovellMaker.Core.Audio.AudioGroup group ? group : default, Amount = kind == ScreenButtonAction.Volume ? (double)(audioAmount.Value ?? 0) / 100 : 0 }).ToImmutableArray() }));
    }
    private void Subscribe() { if (session is not null) { session.PropertyChanged -= Changed; session.PropertyChanged += Changed; } }
    private void Unsubscribe() { if (session is not null) session.PropertyChanged -= Changed; }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(EditorSession.SelectedScreen) or nameof(EditorSession.SelectedScreenNode) or nameof(EditorSession.CanEdit)) Refresh(); }
    private void Refresh()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var rawNode = session?.SelectedScreenNode;
            var node = rawNode is null ? null : session?.ScreenPresentation?.Nodes.FirstOrDefault(n => n.Id == rawNode.Id) ?? rawNode; var screen = session?.SelectedScreen;
            body.IsEnabled = session?.CanEdit == true && screen is not null;
            title.Text = node is null ? "Экран" : ComponentName(node.Kind); name.Text = node?.Name ?? ""; text.Text = node?.Text ?? "";
            textField.IsVisible = node?.Kind is ScreenNodeKind.Text or ScreenNodeKind.Button; buttonFields.IsVisible = node?.Kind is ScreenNodeKind.Button or ScreenNodeKind.Area;
            text.IsReadOnly = node?.Dialogue != DialogueBinding.None;
            var bindings = new List<BindingOption> { new(DialogueBinding.None, "Обычный компонент") };
            if (node?.ParentId is not null && node.Kind == ScreenNodeKind.Panel) bindings.AddRange([new(DialogueBinding.Panel, "Панель диалога"), new(DialogueBinding.CssPanel, "Панель · размещение из CSS")]);
            if (node?.Kind == ScreenNodeKind.Text) bindings.AddRange([new(DialogueBinding.Text, "Текст текущей реплики"), new(DialogueBinding.Speaker, "Имя говорящего")]);
            dialogueBinding.ItemsSource = bindings; dialogueBinding.SelectedItem = bindings.FirstOrDefault(b => b.Value == node?.Dialogue);
            dialogueBinding.IsEnabled = node?.ParentId is not null; dialogueFields.IsVisible = node is { Dialogue: not DialogueBinding.None };
            if (session?.ActiveScene?.Style is { } style)
            {
                dialogueFontSize.Value = (decimal)style.FontSize; dialogueTextColor.Text = style.TextColor;
                dialogueBackground.Text = style.BackgroundColor; dialogueBorder.Text = style.BorderColor;
                dialogueBorderWidth.Value = (decimal)style.BorderWidth; dialogueRadius.Value = (decimal)style.BorderRadius;
            }
            var values = new[] { node?.Bounds.X ?? 0, node?.Bounds.Y ?? 0, node?.Bounds.Width ?? 1, node?.Bounds.Height ?? 1 }; for (var i = 0; i < 4; i++) geometry[i].Value = (decimal)values[i];
            visible.IsChecked = node?.Visible == true; enabled.IsChecked = node?.Enabled == true;
            clipping.IsChecked = node?.ClipChildren == true; color.Text = node?.Color ?? "#FFFFFF"; orderValue.Value = node?.Order ?? 0;
            var g = node?.Geometry ?? new();
            var transformValues = new[] { g.AnchorX, g.AnchorY, g.PivotX, g.PivotY, g.ScaleX, g.ScaleY, g.Rotation, g.Opacity };
            for (var i = 0; i < transform.Length; i++) transform[i].Value = (decimal)transformValues[i];
            var descendants = node is not null && screen is not null ? ScreenTree.Subtree(screen.Definition(), node.Id) : System.Collections.Immutable.ImmutableHashSet<Guid>.Empty;
            var parents = screen?.Nodes.Where(n => !descendants.Contains(n.Id) && n.Kind is ScreenNodeKind.Container or ScreenNodeKind.Panel)
                .Select(n => new Option(n.Id, string.IsNullOrEmpty(n.Name) ? ComponentName(n.Kind) + " · " + n.Id.ToString("N")[..6] : n.Name)).ToArray() ?? [];
            parent.ItemsSource = parents; parent.SelectedItem = parents.FirstOrDefault(p => p.Id == node?.ParentId); parent.IsEnabled = node?.ParentId is not null;
            var images = new[] { new Option(null, "Без изображения") }.Concat(session?.Project?.Images.Select(i => new Option(i.Id, i.Name)) ?? []).ToArray();
            sprite.ItemsSource = images; sprite.SelectedItem = images.FirstOrDefault(i => i.Id == node?.ImageId);
            spritePreview.Asset = session?.Project?.Images.FirstOrDefault(i => i.Id == node?.ImageId); spritePreview.IsVisible = spritePreview.Asset is not null; spritePreview.ProjectDirectory = session?.ProjectDirectory; spritePreview.Revision = session?.AssetRevision ?? 0;
            var screens = session?.Project?.Screens.Select(s => new Option(s.Id, s.Name)).ToArray() ?? [];
            var binding = screen?.Buttons.FirstOrDefault(b => b.NodeId == node?.Id);
            action.SelectedIndex = (int)(binding?.Action ?? ScreenButtonAction.Lua);
            slotFields.IsVisible = binding?.Action is ScreenButtonAction.SaveSlot or ScreenButtonAction.LoadSlot or ScreenButtonAction.DeleteSlot;
            slotTitleField.IsVisible = binding?.Action == ScreenButtonAction.SaveSlot;
            slotId.Text = binding?.SlotId ?? ""; slotTitle.Text = binding?.SlotTitle ?? "";
            audioFields.IsVisible = binding?.Action is ScreenButtonAction.Volume or ScreenButtonAction.Mute;
            audioGroup.SelectedItem = binding?.Group ?? NovellMaker.Core.Audio.AudioGroup.Master;
            audioAmount.Value = (decimal)(binding?.Amount ?? 0) * 100; audioAmount.IsVisible = binding?.Action == ScreenButtonAction.Volume;
            var previous = (target.SelectedItem as Option)?.Id;
            target.ItemsSource = screens; target.SelectedItem = screens.FirstOrDefault(s => s.Id == binding?.TargetScreenId) ?? screens.FirstOrDefault(s => s.Id == previous) ?? screens.FirstOrDefault(); target.IsEnabled = action.SelectedIndex is 2 or 3 or 6 or 12 or 13; targetField.IsVisible = target.IsEnabled;
            startScreen.IsChecked = screen is not null && session?.Project?.StartScreenSceneId == screen.Id;
            screenName.Text = screen?.Name ?? "";
            pauseBelow.IsChecked = screen?.PauseOnOverlay == true; blockBelow.IsChecked = screen?.BlockInputOnOverlay == true;
            var handlers = new[] { new Option(null, "Без обработчика") }.Concat(session?.Project?.Scripts.Select(s => new Option(s.Id, s.Name)) ?? []).ToArray(); handler.ItemsSource = handlers; handler.SelectedItem = handlers.FirstOrDefault(s => s.Id == screen?.HandlerScriptId);
            shortcuts.Text = string.Join("\n", session?.Project?.InputBindings.Select(b => b.Key + " → " + b.Action + (b.TargetSceneId is { } targetId ? " · " + session.Project.Scenes.First(s => s.Id == targetId).Name : "")) ?? []);
            refs.Text = screen is null ? "" : string.Join("\n", screen.References.Select(r => "refs." + r.Key + " → " + ComponentName(screen.Nodes.First(n => n.Id == r.Value).Kind)));
        }
        finally { refreshing = false; }
    }
    internal static string ComponentName(ScreenNodeKind kind) => kind switch { ScreenNodeKind.Panel => "Панель", ScreenNodeKind.Button => "Кнопка", ScreenNodeKind.Text => "Текст", ScreenNodeKind.Image => "Изображение", ScreenNodeKind.Area => "Интерактивная область", _ => "Контейнер" };
}
