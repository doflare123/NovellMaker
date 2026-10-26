using System.Collections.Immutable;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Assets;
using SkiaSharp;
using NovellMaker.Core.Styling;
using NovellMaker.Editor.Views;
using NovellMaker.Editor.Services;
using NovellMaker.Editor.Services.Assets;
using NovellMaker.Editor.Services.Preview;
using GamePreviewRequest = NovellMaker.Editor.Services.Preview.PreviewRequest;
using Avalonia.Controls.Documents;
using NovellMaker.Editor.Services.Git;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Tests;

public sealed class UiAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, ShouldRenderOnUIThread = true });
}

[TestClass]
[DoNotParallelize]
public sealed class EditorWindowTests
{
    [TestMethod]
    [DataRow(LuaValueKind.Null, false)]
    [DataRow(LuaValueKind.Boolean, false)]
    [DataRow(LuaValueKind.Number, false)]
    [DataRow(LuaValueKind.String, false)]
    [DataRow(LuaValueKind.List, false)]
    [DataRow(LuaValueKind.Object, false)]
    [DataRow(LuaValueKind.Boolean, true)]
    [DataRow(LuaValueKind.String, true)]
    public Task ChangingMinigameRatePreservesSchemaKindsAndStructuredCompletion(
        LuaValueKind kind, bool allSchemas) => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _);
        dialogs.CodeEditor.OpenOnCreation = false;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.OpenScreenLibrary();
        await session.CreateScreenHandlerAsync();
        var (json, lua) = kind switch
        {
            LuaValueKind.Null => ("null", "ui.null"),
            LuaValueKind.Boolean => ("true", "true"),
            LuaValueKind.Number => ("42", "42"),
            LuaValueKind.String => ("\"result\"", "'result'"),
            LuaValueKind.List => ("[1,2]", "ui.list({1,2})"),
            _ => ("{\"score\":42}", "{score=42}"),
        };
        var value = LuaValue.FromJson(json);
        var schema = new MinigameSchema { Kind = kind,
            Fields = kind == LuaValueKind.Object
                ? ImmutableDictionary<string, LuaValueKind>.Empty.Add("score", LuaValueKind.Number)
                : ImmutableDictionary<string, LuaValueKind>.Empty };
        var original = new MinigameDefinition
        {
            ParametersSchema = allSchemas ? schema : new(), StateSchema = allSchemas ? schema : new(), ResultSchema = schema,
            DefaultParameters = allSchemas ? value : LuaValue.FromObject([]),
            InitialState = allSchemas ? value : LuaValue.FromObject([]),
        };
        session.UpdateScreen(session.SelectedScreen! with { Minigame = original });
        Dispatcher.UIThread.RunJobs();
        Find<Expander>(window, "MinigameResourceExpander").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Find<NumericUpDown>(window, "MinigameUpdateRate").Value = 10;
        Find<Button>(window, "ApplyMinigameResource").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        var updated = session.SelectedScreen!.Minigame!;
        Assert.AreEqual(10d, updated.UpdatesPerSecond);
        Assert.AreEqual(original.ParametersSchema.Kind, updated.ParametersSchema.Kind);
        Assert.AreEqual(original.StateSchema.Kind, updated.StateSchema.Kind);
        Assert.AreEqual(original.ResultSchema.Kind, updated.ResultSchema.Kind);
        CollectionAssert.AreEquivalent(original.ResultSchema.Fields.ToArray(), updated.ResultSchema.Fields.ToArray());
        Assert.AreEqual(original.DefaultParameters.ToJson(), updated.DefaultParameters.ToJson());
        Assert.AreEqual(original.InitialState.ToJson(), updated.InitialState.ToJson());
        session.Undo(); Assert.AreEqual(original, session.SelectedScreen!.Minigame);
        session.Redo(); Assert.AreEqual(updated, session.SelectedScreen!.Minigame);

        var path = Path.Combine(Path.GetTempPath(), "minigame-schema-" + Guid.NewGuid().ToString("N") + ".novell");
        try
        {
            await new JsonProjectStore().SaveAsync(path, session.Project!);
            var reloaded = await new JsonProjectStore().LoadAsync(path);
            var resource = reloaded.Scenes.Single(s => s.Id == session.SelectedScreen.Id).AsScreen();
            Assert.AreEqual(original.ResultSchema.Kind, resource.Minigame!.ResultSchema.Kind);
            var stack = new ScreenStack(Guid.NewGuid());
            var instance = stack.Open(Guid.NewGuid(), resource.Definition());
            await using var scripts = new ScreenLuaSession(stack);
            scripts.Attach(instance, "return {on_event=function() ui.complete_minigame(" + lua + ") end}",
                state: resource.Minigame.InitialState, minigame: resource.Minigame,
                invocation: new(resource.Minigame.DefaultParameters, false));
            async Task Drain()
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (scripts.HasPending)
                {
                    timeout.Token.ThrowIfCancellationRequested(); scripts.Poll();
                    if (scripts.PendingTask is { } task) await task; else await Task.Delay(1);
                }
                Assert.IsEmpty(scripts.Diagnostics);
            }
            await Drain();
            scripts.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty);
            await Drain();
            Assert.AreEqual(value.ToJson(), scripts.GetMinigameResult(instance)!.Data.ToJson());
        }
        finally { File.Delete(path); }
        window.Close();
    });

    [TestMethod]
    public Task MinigameResourceAndPuzzleCanBeCreatedEditedAndUndoneThroughInspector() => WithUi(async () =>
    {
        using var session=CreateSession(out _,out _);var window=CreateWindow(session);window.Show();
        await session.NewProjectAsync();session.AddScene();session.OpenScreenLibrary();Dispatcher.UIThread.RunJobs();var before=session.Project!;
        Find<Button>(window,"AddMinigameExample").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(before.Scenes.Length+3,session.Project!.Scenes.Length);session.Undo();Assert.IsTrue(before.ContentEquals(session.Project));session.Redo();
        var puzzle=session.Project!.Scenes.Last();session.OpenScreenLibrary(puzzle.Id);Dispatcher.UIThread.RunJobs();
        Find<Expander>(window,"MinigameResourceExpander").IsExpanded=true;Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(Find<CheckBox>(window,"MinigameEnabled").IsChecked);
        Find<NumericUpDown>(window,"MinigameUpdateRate").Value=10;
        Find<CheckBox>(window,"MinigameCanSave").IsChecked=false;
        Find<TextBox>(window,"MinigameSaveReason").Text="Сначала закончите раунд";
        Find<Button>(window,"ApplyMinigameResource").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(10d,session.SelectedScreen!.Minigame!.UpdatesPerSecond);Assert.IsFalse(session.SelectedScreen.Minigame.CanSave);
        session.Undo();Assert.AreEqual(5d,session.SelectedScreen!.Minigame!.UpdatesPerSecond);session.Redo();
        window.UpdateLayout();AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);Dispatcher.UIThread.RunJobs();
        if(Environment.GetEnvironmentVariable("NOVELLMAKER_CAPTURE_DIR") is {} directory)
        {using var bitmap=new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Vector(96,96));bitmap.Render(window);bitmap.Save(Path.Combine(directory,"editor-minigame-puzzle.png"),PngBitmapEncoderOptions.Default);}
        window.Close();
    });

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)]
    public Task AuthorExampleCanBeAddedThroughInspectorAndEditedOnCanvas(int exampleKind) => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.OpenScreenLibrary(); Dispatcher.UIThread.RunJobs();
        var inspector = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single(); var before = session.Project!;
        Find<Button>(inspector, exampleKind == 6 ? "AddLuaDiagnosticsExample" : exampleKind == 5 ? "AddLuaRandomExample" : exampleKind == 4 ? "AddLuaLibraryExample" : exampleKind == 3 ? "AddLuaModulesExample" : exampleKind == 2 ? "AddComponentStateExample" : exampleKind == 1 ? "AddTriggersExample" : "AddPlayerSettingsExample").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(before.Scenes.Length + 1, session.Project!.Scenes.Length);
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); session.Redo();
        session.OpenScreenLibrary(session.Project!.Scenes.Last().Id); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.SelectedScreen!.Nodes.Any(n => n.Text == (exampleKind == 6 ? "Журнал Lua" : exampleKind == 5 ? "Управляемый RNG" : exampleKind == 4 ? "Библиотека Lua" : exampleKind == 3 ? "Модули Lua" : exampleKind == 2 ? "Состояние компонентов" : exampleKind == 1 ? "Триггеры и постоянные данные" : "Настройки")));
        window.UpdateLayout();
        await Task.Yield();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        var screenshot = exampleKind == 6 ? "editor-lua-diagnostics-example.png" : exampleKind == 5 ? "editor-lua-rng-example.png" : exampleKind == 4 ? "editor-lua-standard-library.png" : exampleKind == 3 ? "editor-lua-modules.png" : exampleKind == 2 ? "editor-component-state.png" : exampleKind == 1 ? "editor-triggers.png" : "editor-player-settings.png";
        Capture(window, screenshot);
        if (exampleKind >= 4 && Environment.GetEnvironmentVariable("NOVELLMAKER_CAPTURE_DIR") is { } directory)
        {
            // Render this window's current visual tree, avoiding a cached frame from a prior headless window.
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(Path.Combine(directory, screenshot), PngBitmapEncoderOptions.Default);
        }
        window.Close();
    });

    [TestMethod]
    public Task ModuleReferenceCanBeBoundRenamedAndUndoneThroughTheInspector() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddLuaModulesExample();
        var handler = session.Project!.Scripts.Single(s => s.Id == session.Project.Scenes.Last().Canvas.HandlerScriptId);
        var child = session.Project.Scripts.Single(s => s.Id == handler.References["counter"]);
        session.OpenScriptLibrary(handler.Id); Dispatcher.UIThread.RunJobs();
        Find<Expander>(window, "LuaReferencesSettings").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Find<TextBox>(window, "LuaAssetReferenceAlias").Text = "renamed_alias";
        var target = Find<ComboBox>(window, "LuaAssetReferenceTarget");
        target.SelectedItem = target.ItemsSource!.Cast<object>().Single(t => t.ToString() == "Lua: " + child.Name);
        Find<Button>(window, "InsertLuaAssetReference").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(child.Id, session.SelectedScript!.References["renamed_alias"]);
        StringAssert.Contains(Find<TextBlock>(window, "LuaAssetReferences").Text!, "modules.renamed_alias");
        session.Undo(); Assert.IsFalse(session.SelectedScript!.References.ContainsKey("renamed_alias")); session.Redo();
        session.OpenScriptLibrary(child.Id); session.UpdateScript(child with { Name = "new-name.lua" });
        session.OpenScriptLibrary(handler.Id); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(child.Id, session.SelectedScript!.References["renamed_alias"]);
        Find<Expander>(window, "LuaReferencesSettings").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        StringAssert.Contains(Find<TextBlock>(window, "LuaAssetReferences").Text!, "new-name.lua");
        Capture(window, "editor-lua-module-references.png"); window.Close();
    });

    [TestMethod]
    public Task NamedSlotCommandsAndExampleAreEditableInActualInspector() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Button, 200, 200);
        Dispatcher.UIThread.RunJobs(); var inspector = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
        var action = Find<ComboBox>(inspector, "ScreenButtonAction"); action.SelectedIndex = 18;
        Find<TextBox>(inspector, "ScreenSlotId").Text = "chapter-1"; Find<TextBox>(inspector, "ScreenSlotTitle").Text = "Глава 1";
        Assert.AreEqual("chapter-1", session.SelectedScreen!.Buttons.Single().SlotId); Assert.AreEqual("Глава 1", session.SelectedScreen.Buttons.Single().SlotTitle);
        action.SelectedIndex = 19; Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.LoadSlot, session.SelectedScreen!.Buttons.Single().Action);
        action.SelectedIndex = 20; Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.DeleteSlot, session.SelectedScreen!.Buttons.Single().Action);
        session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(19, action.SelectedIndex);
        action.SelectedIndex = 16; Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.ContinueGame, session.SelectedScreen!.Buttons.Single().Action);
        Assert.IsNull(session.SelectedScreen.Buttons.Single().SlotId);
        var before = session.Project; Find<Button>(inspector, "AddGameMenusExample").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(before!.Scenes.Length + 2, session.Project!.Scenes.Length); Assert.IsNotNull(session.Project.StartScreenSceneId);
        session.Undo(); Assert.IsTrue(before.ContentEquals(session.Project)); session.Redo();
        Capture(window, "editor-game-slots.png"); window.Close();
    });

    [TestMethod]
    public Task StartupScreenAndSessionCommandsAreEditableInActualInspector() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); var story = session.ActiveScene!.Id; session.AddScene(); var menu = session.ActiveScene!.Id;
        session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Button, 200, 200); Dispatcher.UIThread.RunJobs();
        var inspector = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
        Find<CheckBox>(inspector, "ProjectStartScreen").IsChecked = true;
        Assert.AreEqual(menu, session.Project!.StartScreenSceneId);
        session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.IsFalse(Find<CheckBox>(inspector, "ProjectStartScreen").IsChecked);
        session.Redo(); Dispatcher.UIThread.RunJobs(); Assert.IsTrue(Find<CheckBox>(inspector, "ProjectStartScreen").IsChecked);
        Find<ComboBox>(inspector, "ScreenButtonAction").SelectedIndex = 13;
        Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.NewGame, session.SelectedScreen!.Buttons.Single().Action);
        Assert.IsTrue(session.Project.Scenes.Any(s => s.Id == session.SelectedScreen.Buttons.Single().TargetScreenId));
        var target = Find<ComboBox>(inspector, "ScreenButtonTarget");
        var previousTarget = session.SelectedScreen.Buttons.Single().TargetScreenId;
        target.SelectedIndex = (target.SelectedIndex + 1) % target.ItemCount;
        var changedTarget = session.SelectedScreen.Buttons.Single().TargetScreenId;
        Assert.AreNotEqual(previousTarget, changedTarget);
        session.Undo(); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(previousTarget, session.SelectedScreen!.Buttons.Single().TargetScreenId);
        session.Redo(); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(changedTarget, session.SelectedScreen!.Buttons.Single().TargetScreenId);
        Find<ComboBox>(inspector, "ScreenButtonAction").SelectedIndex = 14;
        Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.MainMenu, session.SelectedScreen!.Buttons.Single().Action);
        Assert.IsNull(session.SelectedScreen.Buttons.Single().TargetScreenId);
        Find<ComboBox>(inspector, "ScreenButtonAction").SelectedIndex = 15;
        Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.EndSession, session.SelectedScreen!.Buttons.Single().Action);
        Capture(window, "editor-session-startup.png"); window.Close();
    });

    [TestMethod]
    public Task DialoguePaletteInspectorAndCanvasGestureUseOneHistoryAndSharedRasterizer() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _); dialogs.AcceptDeletion = true;
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene();
        var document = session.ActiveScene!; document.AddLine(); document.LineText = "Диалог §#FF0000на холсте§r";
        session.OpenScreenLibrary(); Dispatcher.UIThread.RunJobs();
        var panel = session.SelectedScreen!.Nodes.Single(n => n.Dialogue == NovellMaker.Core.Screens.DialogueBinding.CssPanel);
        session.SelectScreenNode(panel.Id); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(2, window.GetVisualDescendants().OfType<DialogueComponentView>().Count());
        var canvas = window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single().Artboard;
        var shown = session.ScreenPresentation!.Nodes.Single(n => n.Id == panel.Id);
        Point At(double x, double y) => canvas.TranslatePoint(new Point(x, y), window)!.Value;
        var before = session.Project;
        window.MouseDown(At(shown.Bounds.X + 5, shown.Bounds.Y + 5), MouseButton.Left);
        window.MouseMove(At(shown.Bounds.X + 45, shown.Bounds.Y - 55));
        Assert.AreSame(before, session.Project);
        window.MouseUp(At(shown.Bounds.X + 45, shown.Bounds.Y - 55), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(NovellMaker.Core.Screens.DialogueBinding.Panel, session.SelectedScreenNode!.Dialogue);
        Assert.AreEqual(shown.Bounds.X + 40, session.SelectedScreenNode.Bounds.X, 1);
        Assert.AreEqual(shown.Bounds.Y - 60, session.SelectedScreenNode.Bounds.Y, 1);
        session.Undo(); Assert.IsTrue(before!.ContentEquals(session.Project)); session.Redo(); Dispatcher.UIThread.RunJobs();
        var inspector = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
        Find<NumericUpDown>(inspector, "DialogueFontSize").Value = 32;
        Assert.AreEqual(32d, document.Style!.FontSize); Assert.IsTrue(document.UseSceneStyle);
        var css = document.Css;
        Find<TextBox>(inspector, "DialogueTextColor").Text = "ошибка";
        Assert.AreEqual(css, document.Css); Assert.IsFalse(string.IsNullOrWhiteSpace(Find<TextBlock>(inspector, "ScreenEditorStatus").Text));
        Find<TextBox>(inspector, "DialogueTextColor").Text = "#FFCC88"; Assert.AreEqual("#FFCC88", document.Style.TextColor);
        var text = session.SelectedScreen.Nodes.Single(n => n.Dialogue == NovellMaker.Core.Screens.DialogueBinding.Text);
        session.SelectScreenNode(text.Id); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(Find<TextBox>(inspector, "ScreenNodeText").IsReadOnly);
        Assert.AreEqual(document.LineText, Find<TextBox>(inspector, "ScreenNodeText").Text);
        var bindings = Find<ComboBox>(inspector, "DialogueBinding"); bindings.SelectedIndex = 0;
        Assert.AreEqual(NovellMaker.Core.Screens.DialogueBinding.None, session.SelectedScreenNode!.Dialogue);
        session.Undo(); Assert.AreEqual(NovellMaker.Core.Screens.DialogueBinding.Text, session.SelectedScreenNode!.Dialogue);
        var count = session.SelectedScreen.Nodes.Length;
        Find<Button>(window, "ToolboxDialogue").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(count + 3, session.SelectedScreen.Nodes.Length);
        await session.DeleteScreenNodeAsync(session.SelectedScreenNode!.Id); Assert.AreEqual(count, session.SelectedScreen.Nodes.Length);
        session.Undo(); Assert.AreEqual(count + 3, session.SelectedScreen.Nodes.Length);
        Assert.AreSame(document, session.SelectedDocument);
        Capture(window, "editor-stage4-dialogue.png"); window.Close();
    });

    [TestMethod]
    public Task TransformedDesignerUsesLocalGesturesAndInspectorHierarchyAtTwoWindowSizes() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _); dialogs.AcceptDeletion = true;
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene();
        session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Panel, 150, 120); var panel = session.SelectedScreenNode!;
        session.UpdateScreenNode(panel with { Bounds = new(150, 120, 400, 300), Geometry = new() { Rotation = 25, ScaleX = 1.1, ScaleY = .8 }, Color = "#80DDFF", ClipChildren = false });
        var definition = session.SelectedScreen!.Definition(); var point = definition.Visual(panel.Id).World.Apply(40, 60);
        session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Button, point.X, point.Y); var node = session.SelectedScreenNode!;
        Assert.AreEqual(panel.Id, node.ParentId); Assert.AreEqual(40, node.Bounds.X, 1e-7); Assert.AreEqual(60, node.Bounds.Y, 1e-7);
        session.UpdateScreenNode(node with { Text = "Кириллица · поворот", Bounds = node.Bounds with { Height = 65 }, Geometry = new() { Rotation = -10, PivotX = .5, PivotY = .5 } });
        Dispatcher.UIThread.RunJobs(); var surface = window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single(); var canvas = surface.Artboard;
        Point At(NovellMaker.Core.Screens.ScreenPoint p) => canvas.TranslatePoint(new(p.X, p.Y), window)!.Value;
        definition = session.SelectedScreen.Definition(); var center = definition.Visual(node.Id).World.Apply(80, 30);
        var parent = definition.Visual(panel.Id).World; var start = parent.Apply(0, 0); var end = parent.Apply(30, 20);
        window.MouseDown(At(center), MouseButton.Left); window.MouseMove(At(new(center.X + end.X - start.X, center.Y + end.Y - start.Y)));
        window.MouseUp(At(new(center.X + end.X - start.X, center.Y + end.Y - start.Y)), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(70, session.SelectedScreenNode!.Bounds.X, 1); Assert.AreEqual(80, session.SelectedScreenNode.Bounds.Y, 1);
        session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(node.Bounds.X, session.SelectedScreenNode!.Bounds.X, 1e-7);
        definition = session.SelectedScreen.Definition(); var currentNode = session.SelectedScreenNode!;
        var corner = definition.Visual(node.Id).World.Apply(currentNode.Bounds.Width, currentNode.Bounds.Height);
        var enlarged = definition.Visual(node.Id).World.Apply(currentNode.Bounds.Width + 40, currentNode.Bounds.Height + 20);
        window.MouseDown(At(corner), MouseButton.Left); window.MouseMove(At(enlarged)); window.MouseUp(At(enlarged), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(currentNode.Bounds.Width + 40, session.SelectedScreenNode!.Bounds.Width, 1); Assert.AreEqual(currentNode.Bounds.Height + 20, session.SelectedScreenNode.Bounds.Height, 1);
        session.Undo(); Dispatcher.UIThread.RunJobs();
        var properties = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
        Find<Expander>(properties, "ScreenGeometryExpander").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Find<NumericUpDown>(properties, "ScreenRotation").Value = 35; Find<NumericUpDown>(properties, "ScreenOpacity").Value = .6m;
        Find<TextBox>(properties, "ScreenNodeColor").Text = "#FF80C0"; Assert.AreEqual(35, session.SelectedScreenNode.Geometry.Rotation); Assert.AreEqual(.6, session.SelectedScreenNode.Geometry.Opacity);
        Find<Button>(properties, "CopyScreenNode").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Assert.HasCount(4, session.SelectedScreen.Nodes);
        var copied = session.SelectedScreenNode!; var parentCombo = Find<ComboBox>(properties, "ScreenNodeParent"); parentCombo.SelectedIndex = 0;
        Assert.AreEqual(session.SelectedScreen.Nodes[0].Id, session.SelectedScreenNode!.ParentId);
        session.Undo(); Assert.AreEqual(panel.Id, session.SelectedScreen!.Nodes.Single(n => n.Id == copied.Id).ParentId);
        Find<Expander>(properties, "ScreenGeometryExpander").IsExpanded = false;
        foreach (var scroll in properties.GetVisualDescendants().OfType<ScrollViewer>()) scroll.Offset = default;
        foreach (var size in new[] { new Size(1360, 850), new Size(1040, 720) })
        { window.Width = size.Width; window.Height = size.Height; Dispatcher.UIThread.RunJobs(); Capture(window, $"editor-geometry-{size.Width}.png"); }
        window.Close();
    });
    [TestMethod]
    public Task ProjectShortcutsAndAudioCommandsAreAuthoredThroughInspectorWithUndo() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene();
        session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Button, 100, 100); Dispatcher.UIThread.RunJobs();
        var properties = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
        Find<ComboBox>(properties, "ScreenButtonAction").SelectedIndex = (int)NovellMaker.Core.Screens.ScreenButtonAction.Volume;
        Find<ComboBox>(properties, "ScreenAudioGroup").SelectedItem = NovellMaker.Core.Audio.AudioGroup.Voice;
        Find<NumericUpDown>(properties, "ScreenAudioAmount").Value = -20;
        Assert.AreEqual(-.2, session.SelectedScreen!.Buttons.Single().Amount);
        Assert.AreEqual(NovellMaker.Core.Audio.AudioGroup.Voice, session.SelectedScreen.Buttons.Single().Group);
        Find<Expander>(properties, "ProjectKeysExpander").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Find<TextBox>(properties, "ProjectKey").Text = "F9";
        Find<Button>(properties, "SetProjectKey").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(new NovellMaker.Core.Screens.ProjectInputBinding("F9", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, session.ActiveScene!.Id), session.Project!.InputBindings.Single());
        session.Undo(); Assert.IsEmpty(session.Project.InputBindings); session.Redo(); Assert.HasCount(1, session.Project.InputBindings);
        Find<TextBox>(properties, "ProjectKey").Text = "F13"; var before = session.Project;
        Find<Button>(properties, "SetProjectKey").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.AreSame(before, session.Project); Assert.IsFalse(string.IsNullOrEmpty(Find<TextBlock>(properties, "ScreenEditorStatus").Text));
        window.Close();
    });
    [TestMethod]
    public Task ComponentDepthErrorIsContainedByActualDropHandlerAndKeepsUnsavedWork() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); Dispatcher.UIThread.RunJobs();
        var artboard = window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single().Artboard;
        using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(new ComponentDrop(NovellMaker.Core.Screens.ScreenNodeKind.Container).Encode(session.Project!.Id)));
        void Drop() => artboard.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, artboard, new Point(50, 50), KeyModifiers.None));
        for (var i = 0; i < 31; i++) Drop();
        var before = session.Project; var selection = session.SelectedScreenNodeId;
        Drop(); Dispatcher.UIThread.RunJobs();
        Assert.AreSame(before, session.Project); Assert.AreEqual(selection, session.SelectedScreenNodeId); Assert.IsTrue(session.IsDirty);
        StringAssert.Contains(Find<TextBlock>(window, "ComponentAddError").Text!, "не добавлен");
        session.Undo(); Assert.HasCount(31, session.SelectedScreen!.Nodes);
        window.Close();
    });
    [TestMethod]
    public Task SameSceneDocumentContainsCanvasDialogueAndAllComponentsWithoutScreenLibrary() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); Click(window, Find<Button>(window, "AddSceneButton"));
        var document = session.ActiveScene!; Assert.IsTrue(session.IsScreenDesigner);
        Assert.IsTrue(window.WorkspaceLayout.IsVisible("explorer")); Assert.IsTrue(window.WorkspaceLayout.IsVisible("tools"));
        Assert.IsNotNull(window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single());
        session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Button, 300, 200);
        document.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "AddLineButton")); TypeInto(window, Find<TextBox>(window, "LineTextBox"), "Одна сцена");
        document.SelectedSection = 6; Dispatcher.UIThread.RunJobs();
        Assert.AreSame(document, session.SelectedDocument); Assert.HasCount(2, session.Documents);
        Assert.HasCount(1, session.Project!.Scenes); Assert.HasCount(5, session.Project.Scenes[0].Canvas.Nodes);
        Assert.AreEqual("Одна сцена", document.LineText);
        var sample = window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single().GetVisualDescendants().OfType<DialogueStyleSample>().Single();
        Assert.AreSame(session, sample.DataContext);
        Assert.AreEqual(document.SelectedLine!.Id, sample.SampleFrame?.LineId);
        Assert.AreEqual("Одна сцена", sample.SampleText);
        Assert.IsFalse(session.Explorer[0].Children.Any(e => e.IsScreenLibrary));
        Capture(window, "editor-unified-scene.png"); window.Close();
    });

    [TestMethod]
    public Task VisualDesignerDropsMovesResizesCancelsAndEditsPropertiesImmediately() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard; dialogs.AcceptDeletion = true;
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0; session.ActiveScene!.AddLine(); session.OpenScreenLibrary();
        Dispatcher.UIThread.RunJobs(); Click(window, Find<Button>(window, "AddSceneButton"));
        Assert.IsTrue(window.WorkspaceLayout.IsVisible("tools")); Assert.IsFalse(window.WorkspaceLayout.IsVisible("scene"));
        Assert.HasCount(1, session.SelectedScreen!.Nodes);
        var surface = window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single(); var canvas = surface.Artboard;
        using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(new ComponentDrop(NovellMaker.Core.Screens.ScreenNodeKind.Button).Encode(session.Project!.Id)));
        Assert.AreSame(session, surface.DataContext); Assert.IsTrue(session.CanEdit);
        Assert.IsNotNull(ComponentDrop.Decode(data.TryGetText(), session));
        canvas.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, canvas, new Point(250, 200), KeyModifiers.None)); Dispatcher.UIThread.RunJobs();
        Assert.HasCount(2, session.SelectedScreen.Nodes); var node = session.SelectedScreenNode!;
        Assert.AreEqual(250d, node.Bounds.X); Assert.AreEqual(200d, node.Bounds.Y);
        var before = session.Project;
        Point At(double x, double y) => canvas.TranslatePoint(new Point(x, y), window)!.Value;
        window.MouseDown(At(300, 220), MouseButton.Left); window.MouseMove(At(360, 250));
        Assert.AreSame(before, session.Project, "Dragging previews locally until release.");
        window.MouseUp(At(360, 250), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(310d, session.SelectedScreenNode!.Bounds.X); Assert.AreEqual(230d, session.SelectedScreenNode.Bounds.Y);
        session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(node.Bounds, session.SelectedScreenNode!.Bounds);
        window.MouseDown(At(450, 248), MouseButton.Left); window.MouseMove(At(510, 278)); window.MouseUp(At(510, 278), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(260d, session.SelectedScreenNode!.Bounds.Width); Assert.AreEqual(78d, session.SelectedScreenNode.Bounds.Height);
        before = session.Project;
        window.MouseDown(At(300, 220), MouseButton.Left); window.MouseMove(At(500, 300)); window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.MouseUp(At(500, 300), MouseButton.Left);
        Assert.AreSame(before, session.Project, "Escape must discard a gesture without an undo entry.");
        var properties = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
        Find<TextBox>(properties, "ScreenNodeName").Text = "Начать игру"; Find<TextBox>(properties, "ScreenNodeText").Text = "Играть";
        Assert.AreEqual("Играть", session.SelectedScreenNode!.Text); Assert.AreEqual("Начать игру", session.SelectedScreenNode.Name);
        Find<NumericUpDown>(properties, "ScreenNodeX").Value = 180; Assert.AreEqual(180d, session.SelectedScreenNode.Bounds.X);
        Find<ComboBox>(properties, "ScreenButtonAction").SelectedIndex = 1;
        Assert.AreEqual(NovellMaker.Core.Screens.ScreenButtonAction.Close, session.SelectedScreen!.Buttons.Single().Action);
        Find<Expander>(properties, "ScreenReferencesExpander").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Find<TextBox>(properties, "ScreenReferenceName").Text = "play"; Click(window, Find<Button>(properties, "AddScreenReference"));
        Assert.AreEqual(node.Id, session.SelectedScreen!.References["play"]);
        Find<TextBox>(properties, "ScreenReferenceName").Text = "bad alias"; Click(window, Find<Button>(properties, "AddScreenReference"));
        Assert.IsFalse(session.SelectedScreen.References.ContainsKey("bad alias"));
        Find<Expander>(properties, "ScreenReferencesExpander").IsExpanded = false;
        foreach (var scroll in properties.GetVisualDescendants().OfType<ScrollViewer>()) scroll.Offset = default;
        var zoom = Find<ComboBox>(window, "ScreenZoom"); zoom.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1280d, Find<Viewbox>(window, "ScreenViewport").Width);
        zoom.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
        Capture(window, "editor-visual-designer.png");
        session.OpenScene(session.Project!.Scenes[0].Id); session.ActiveScene!.SelectedSection = 0; Dispatcher.UIThread.RunJobs(); Assert.IsTrue(window.WorkspaceLayout.IsVisible("scene"));
        session.OpenScreenLibrary(); Dispatcher.UIThread.RunJobs(); Assert.IsTrue(window.WorkspaceLayout.IsVisible("tools"));
        window.Close();
    });

    [TestMethod]
    public Task VisualDesignerImportsDropsSpriteKeepsDocumentAndSupportsDeleteUndo() => WithUi(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "NovellMaker.DesignerTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var image = new SKBitmap(32, 32); image.Erase(new SKColor(35, 155, 210));
            var imagePath = Path.Combine(root, "sprite.png"); using (var stream = File.Create(imagePath)) image.Encode(stream, SKEncodedImageFormat.Png, 100);
            using var session = CreateSession(out var dialogs, out _); dialogs.SavePath = Path.Combine(root, "story.novell"); dialogs.ImagePath = imagePath;
            dialogs.AcceptDeletion = true; dialogs.Decision = UnsavedChangesDecision.Discard; dialogs.CodeEditor.OpenOnCreation = false;
            var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScreen(empty: true); var document = session.SelectedDocument;
            await session.ImportDesignerImageAsync(); Dispatcher.UIThread.RunJobs(); Assert.AreSame(document, session.SelectedDocument);
            var asset = session.Project!.Images.Single(); var canvas = window.GetVisualDescendants().OfType<ScreenDesignSurface>().Single().Artboard;
            using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(new ComponentDrop(NovellMaker.Core.Screens.ScreenNodeKind.Image, asset.Id).Encode(session.Project.Id)));
            canvas.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, canvas, new Point(100, 100), KeyModifiers.None)); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(asset.Id, session.SelectedScreenNode!.ImageId);
            session.UpdateScreenNode(session.SelectedScreenNode with { Color = "#80FF80", Geometry = new() { Rotation = 15, Opacity = .8 } });
            foreach (var preview in window.GetVisualDescendants().OfType<ProjectImageView>()) await preview.PreviewLoad;
            Dispatcher.UIThread.RunJobs(); var properties = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
            Assert.IsTrue(Find<ProjectImageView>(properties, "ScreenSpritePreview").HasImage);
            Find<TextBox>(properties, "ScreenNodeName").Text = "Логотип";
            session.AddScreenNodeAt(NovellMaker.Core.Screens.ScreenNodeKind.Button, 450, 350);
            properties = window.GetVisualDescendants().OfType<ScreenPropertiesView>().Single();
            Find<Border>(properties, "ScreenSpriteDrop").RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, properties, default, KeyModifiers.None));
            Assert.AreEqual(asset.Id, session.SelectedScreenNode!.ImageId);
            Find<ComboBox>(properties, "ScreenButtonAction").SelectedIndex = 1;
            await session.SaveAsync(); Assert.IsFalse(session.IsDirty);
            await session.DeleteImageAsync(asset.Id); Assert.IsTrue(session.Project.Screens.SelectMany(s => s.Nodes).All(n => n.ImageId is null));
            session.Undo(); Assert.AreEqual(asset.Id, session.SelectedScreenNode!.ImageId);
            await session.CreateScreenHandlerAsync(); Dispatcher.UIThread.RunJobs(); Assert.AreSame(document, session.SelectedDocument);
            foreach (var scroll in properties.GetVisualDescendants().OfType<ScrollViewer>()) scroll.Offset = default;
            Dispatcher.UIThread.RunJobs(); Capture(window, "editor-designer-sprites.png"); window.Close();
        }
        finally { Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task StandardActionParametersAndExistingScriptReferenceCanBeChanged() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0; var scene = session.ActiveScene!; scene.AddLine(); scene.SelectedSection = 4;
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => !v.PropertiesOnly);
        var menu = Find<Button>(view, "ScriptEventMenu").ContextMenu!;
        menu.Items.OfType<MenuItem>().First().Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Ожидать"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        properties.GetVisualDescendants().OfType<NumericUpDown>().Single().Value = 4;
        Click(window, Find<Button>(properties, "ApplyScriptBlock"));
        Assert.AreEqual(4d, ((NovellMaker.Core.Execution.WaitAction)session.Project!.Scenes[0].ScriptBlocks.Single().Action).Seconds);
        window.WorkspaceLayout.Hide("information"); Dispatcher.UIThread.RunJobs();
        view = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => !v.PropertiesOnly);
        Click(window, Find<ListBox>(view, "ScriptBlocks").GetVisualDescendants().OfType<ListBoxItem>().Single());
        Assert.IsTrue(window.WorkspaceLayout.IsVisible("information"));
        session.AddScript(); var first = session.SelectedScript!.Id; session.AddScript(); var second = session.SelectedScript!.Id;
        dialogs.ScriptChoice = second;
        await session.AttachExistingScriptAsync(scene.Id, scene.SelectedLine!.Id, NovellMaker.Core.Scenes.ScriptPhase.AfterLine);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(second, ((NovellMaker.Core.Execution.RunScriptAction)session.Project.Scenes[0].ScriptBlocks.Last().Action).ScriptId);
        properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        var resource = properties.GetVisualDescendants().OfType<ComboBox>().Last(); resource.SelectedIndex = 0;
        Click(window, Find<Button>(properties, "ApplyScriptBlock"));
        Assert.AreEqual(first, ((NovellMaker.Core.Execution.RunScriptAction)session.Project.Scenes[0].ScriptBlocks.Last().Action).ScriptId);
        var before = session.Project; dialogs.CancelScriptChoice = true;
        await session.AttachExistingScriptAsync(scene.Id, scene.SelectedLine.Id, NovellMaker.Core.Scenes.ScriptPhase.DuringLine);
        Assert.AreSame(before, session.Project); Capture(window, "editor-action-properties.png"); window.Close();
    });

    [TestMethod]
    public Task StartPageSurvivesHiddenDocumentsAndOpensPersistentRecentProject() => WithUi(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "NovellMaker.StartPage", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var dialogs = new UiDialogs { SavePath = Path.Combine(root, "story.novell"), Decision = UnsavedChangesDecision.Discard };
            var recents = new RecentProjectsStore(Path.Combine(root, "recent.json"));
            using var session = new EditorSession(new JsonProjectStore(), dialogs, new UiGit(), codeEditor: dialogs.CodeEditor, recentProjects: recents);
            var window = CreateWindow(session); window.Show(); window.WorkspaceLayout.Hide("documents"); Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(Find<StartPage>(window, "StartPage").IsVisible);
            Click(window, Find<Button>(window, "StartNewProjectButton"));
            await session.SaveAsync(); await session.CloseProjectAsync(); Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(Find<StartPage>(window, "StartPage").IsVisible);
            Assert.HasCount(1, recents.Read());
            var recent = Find<ItemsControl>(window, "RecentProjectsList").GetVisualDescendants().OfType<Button>().Single();
            Click(window, recent); await UntilAsync(() => session.HasProject && !session.IsBusy);
            Assert.AreEqual(dialogs.SavePath, session.ProjectPath);
            await session.CloseProjectAsync(); Dispatcher.UIThread.RunJobs(); Capture(window, "editor-start-page.png"); window.Close();
        }
        finally { Directory.Delete(root, true); }
    });

    [TestMethod]
    public Task ExistingScriptPickerRequiresChoiceAndSupportsCancel() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        var scripts = new[] { NovellMaker.Core.Execution.LuaScriptAsset.Create("one.lua"), NovellMaker.Core.Execution.LuaScriptAsset.Create("two.lua") };
        var result = new EditorDialogs(window).PickScriptAsync(scripts); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single(); var picker = Find<ListBox>(dialog, "ChooseLuaScript");
        Assert.AreEqual(-1, picker.SelectedIndex); Assert.IsFalse(Find<Button>(dialog, "ConfirmLuaScript").IsEnabled);
        picker.SelectedIndex = 1; Click(dialog, Find<Button>(dialog, "ConfirmLuaScript"));
        Assert.AreEqual(scripts[1].Id, await result);
        result = new EditorDialogs(window).PickScriptAsync(scripts); Dispatcher.UIThread.RunJobs(); dialog = window.OwnedWindows.Single();
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Assert.IsNull(await result); window.Close();
    });

    [TestMethod]
    public Task DroppingScriptAttachesToPhraseAndRejectsForeignProject() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0; var scene = session.ActiveScene!; scene.AddLine(); session.AddScript(); var script = session.SelectedScript!;
        session.InspectPhrase(); Dispatcher.UIThread.RunJobs();
        var target = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(ScriptDragDrop.Encode(session.Project!.Id, script.Id)));
        target.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, target, default, KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
        var block = session.Project.Scenes[0].ScriptBlocks.Single();
        Assert.AreEqual(scene.SelectedLine!.Id, block.LineId); Assert.AreEqual(NovellMaker.Core.Scenes.ScriptPhase.DuringLine, block.Phase);
        Assert.AreEqual(script.Id, ((NovellMaker.Core.Execution.RunScriptAction)block.Action).ScriptId);
        Assert.IsNull(ScriptDragDrop.Decode(ScriptDragDrop.Encode(Guid.NewGuid(), script.Id), session));
        session.Undo(); Assert.IsEmpty(session.Project.Scenes[0].ScriptBlocks); Dispatcher.UIThread.RunJobs();
        var tree = Find<TreeView>(window, "ProjectExplorer");
        var sceneItem = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(t => t.DataContext is ExplorerEntry e && e.SceneId == scene.Id);
        sceneItem.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, sceneItem, default, KeyModifiers.None));
        Assert.AreEqual(NovellMaker.Core.Scenes.ScriptPhase.SceneStart, session.Project.Scenes[0].ScriptBlocks.Single().Phase);
        session.Undo(); scene.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
        var phraseItem = Find<ListBox>(window, "DialogueLines").GetVisualDescendants().OfType<ListBoxItem>().Single();
        phraseItem.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, phraseItem, default, KeyModifiers.None));
        Assert.AreEqual(scene.SelectedLine.Id, session.Project.Scenes[0].ScriptBlocks.Single().LineId);
        Capture(window, "editor-script-attachment.png"); window.Close();
    });

    [TestMethod]
    public Task ScriptNameWithoutExtensionKeepsResourceIdentityAndIcon() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _); dialogs.CodeEditor.OpenOnCreation = false;
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        await session.CreateAndOpenScriptAsync(); Dispatcher.UIThread.RunJobs(); var script = session.SelectedScript!;
        Find<TextBox>(window, "LuaScriptName").Text = "intro"; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual("intro", session.SelectedScript!.Name);
        var entry = session.Explorer[0].Items.SelectMany(e => e.Items).Single(e => e.ScriptId == script.Id);
        Assert.AreEqual("lua", entry.IconKind); Assert.IsEmpty(dialogs.CodeEditor.Opened);
        using var browser = new ProjectFolderBrowser(session);
        browser.Navigate(browser.Roots[0].Items.Single(e => e.IsScriptLibrary));
        browser.SelectedTile = browser.Tiles.Single(); session.UpdateScript(session.SelectedScript with { Name = "renamed" });
        Assert.AreEqual(script.Id, browser.SelectedTile!.Entry.ScriptId);
        Assert.AreEqual("scene", new ExplorerEntry("Сцена", "", [], SceneId: Guid.NewGuid()).IconKind);
        Assert.AreEqual("file", new ExplorerEntry("readme.txt").IconKind); window.Close();
    });

    [TestMethod]
    public Task LuaNameDialogShowsSuggestedNameAcceptsRenameAndCancels() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _);
        var window = CreateWindow(session); window.Show();
        var dialogs = new EditorDialogs(window);
        var result = dialogs.EditLuaNameAsync("script_1.lua"); Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var input = Find<TextBox>(dialog, "LuaNameInput");
        Assert.AreEqual("script_1.lua", input.Text); Assert.IsTrue(input.IsFocused);
        Assert.AreEqual("script_1", input.SelectedText);
        input.Text = "  "; Dispatcher.UIThread.RunJobs(); Assert.IsFalse(Find<Button>(dialog, "AcceptLuaName").IsEnabled);
        input.Text = "opening.lua"; Click(dialog, Find<Button>(dialog, "AcceptLuaName"));
        Assert.AreEqual("opening.lua", await result);
        result = dialogs.EditLuaNameAsync("opening.lua"); Dispatcher.UIThread.RunJobs(); dialog = window.OwnedWindows.Single();
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsNull(await result); window.Close();
    });

    [TestMethod]
    public Task PhraseAddButtonOpensMenuAndAddsActionWithoutLosingDocument() => WithUi(async () =>
    {
        using var session = CreateSession(out _, out _);
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        var scene = session.ActiveScene!; scene.AddLine(); scene.LineText = "Текст"; Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "OpenPhraseScriptButton"));
        var view = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        var add = Find<Button>(view, "ScriptEventMenu");
        Click(window, add);
        var menu = add.ContextMenu!;
        Assert.IsTrue(menu.IsOpen);
        var submenu = menu.Items.OfType<MenuItem>().First();
        submenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Ожидать"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close(); Dispatcher.UIThread.RunJobs();
        Assert.AreSame(scene, session.SelectedDocument);
        Assert.AreEqual("Текст", scene.LineText);
        Assert.IsInstanceOfType<NovellMaker.Core.Execution.WaitAction>(session.Project!.Scenes[0].ScriptBlocks.Single().Action);
        scene.SelectedSection = 4; Dispatcher.UIThread.RunJobs();
        view = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => !v.PropertiesOnly);
        add = Find<Button>(view, "ScriptEventMenu"); Click(window, add);
        menu = add.ContextMenu!; Assert.IsTrue(menu.IsOpen);
        menu.Items.OfType<MenuItem>().First().Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Ожидать"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close(); Dispatcher.UIThread.RunJobs();
        Assert.HasCount(2, session.Project!.Scenes[0].ScriptBlocks);
        window.Close();
    });

    [TestMethod]
    public Task CreatingLuaFromPhrasePropertiesKeepsDocumentAndUnsavedText() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        var scene = session.ActiveScene!; scene.AddLine(); scene.LineText = "Несохранённая реплика";
        var documentCount = session.Documents.Count; Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "OpenPhraseScriptButton"));
        Assert.AreEqual(0, scene.SelectedSection);
        var properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        Find<ListBox>(properties, "ScriptBlocks").ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Name == "CreatePhraseLuaMenu")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => !session.IsBusy && dialogs.CodeEditor.Opened.Count == 1);
        Assert.AreSame(scene, session.SelectedDocument); Assert.AreEqual(documentCount, session.Documents.Count);
        Assert.AreEqual("Несохранённая реплика", scene.LineText); Assert.AreEqual(0, scene.SelectedSection);
        Assert.IsInstanceOfType<NovellMaker.Core.Execution.RunScriptAction>(session.Project!.Scenes[0].ScriptBlocks.Single().Action);
        Assert.IsFalse(window.GetVisualDescendants().OfType<Button>().Any(b => b.Name is "AddLuaBlock" or "OpenLuaLibrary"));
        Capture(window, "editor-phrase-properties.png"); window.Close();
    });

    [TestMethod]
    public Task LuaLibraryEditsReferencesValidatesAndAttachesResourceThroughActualControls() => WithUi(async () =>
    {
        using var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        var sceneId = session.ActiveScene!.Id;
        var document = session.SelectedDocument; var documentCount = session.Documents.Count;
        var explorer = Find<TreeView>(window, "ProjectExplorer");
        var createMenu = explorer.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Name == "CreateLuaResourceMenu");
        createMenu.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => !session.IsBusy && dialogs.CodeEditor.Opened.Count > 0);
        Assert.AreSame(document, session.SelectedDocument); Assert.AreEqual(documentCount, session.Documents.Count);
        Assert.AreEqual(InspectorTarget.Script, session.InspectorTarget);
        await session.RefreshExternalScriptsAsync();
        Dispatcher.UIThread.RunJobs(); Capture(window, "editor-script-properties-before-settings.png");
        Find<Button>(window, "PrepareLuaAnnotations").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !session.IsBusy && dialogs.CodeEditor.Opened.Any(f => f.EndsWith("NovellMakerApi" + Path.DirectorySeparatorChar + "README.md", StringComparison.Ordinal)));
        Assert.IsTrue(File.Exists(Path.Combine(session.ProjectDirectory!, "Scripts", "NovellMakerApi", "ui.lua")));
        Assert.AreSame(document, session.SelectedDocument);
        Find<Button>(window, "OpenLuaJournal").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(() => !session.IsBusy);
        Find<Expander>(window, "CodeEditorSettings").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "ChooseCodeEditor"));
        await UntilAsync(() => !session.IsBusy && dialogs.CodeEditor.Executable is not null);
        Assert.AreEqual("FixtureEditor", session.CodeEditorLabel);
        var openedBeforeScript = dialogs.CodeEditor.Opened.Count;
        await session.OpenScriptExternallyAsync(session.SelectedScript!.Id);
        await UntilAsync(() => !session.IsBusy && dialogs.CodeEditor.Opened.Count == openedBeforeScript + 1);
        Find<Expander>(window, "CodeEditorSettings").IsExpanded = false; Dispatcher.UIThread.RunJobs();
        var id = session.SelectedScript!.Id;
        Find<TextBox>(window, "LuaScriptName").Text = "chapter.lua";
        Assert.IsFalse(window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name is "LuaAssetSource" or "LuaSourceBox"));
        var file = dialogs.CodeEditor.Opened.Last();
        await File.WriteAllTextAsync(file, "game.set('gui_script', 'yes')");
        await UntilAsync(() => session.SelectedScript!.Source.Contains("gui_script") && session.SelectedScriptDiagnostic == "Синтаксис корректен");
        StringAssert.Contains(Find<TextBlock>(window, "LuaAssetStatus").Text!, "Синтаксис корректен");
        await File.WriteAllTextAsync(file, "if then");
        await UntilAsync(() => session.SelectedScript!.Source == "if then" && !session.SelectedScriptDiagnostic.Contains("Синтаксис корректен") && !session.SelectedScriptDiagnostic.Contains("Проверка синтаксиса"));
        await File.WriteAllTextAsync(file, "return " + new string('(', 12000) + "1" + new string(')', 12000));
        await UntilAsync(() => session.SelectedScriptDiagnostic.Contains("вложенности"));
        Assert.IsTrue(window.IsVisible); Assert.AreSame(document, session.SelectedDocument);
        await File.WriteAllTextAsync(file, "game.set('gui_script', 'yes')");
        await UntilAsync(() => session.SelectedScriptDiagnostic == "Синтаксис корректен");
        await File.WriteAllTextAsync(file + ".tmp", "game.set('gui_script', 'yes')\n-- atomic save");
        File.Move(file + ".tmp", file, overwrite: true);
        await UntilAsync(() => session.SelectedScript!.Source.Contains("atomic save") && session.SelectedScriptDiagnostic == "Синтаксис корректен");
        Find<Expander>(window, "LuaReferencesSettings").IsExpanded = true; Dispatcher.UIThread.RunJobs();
        Find<TextBox>(window, "LuaAssetReferenceAlias").Text = "scene";
        Assert.IsNotNull(Find<ComboBox>(window, "LuaAssetReferenceTarget").SelectedItem);
        Capture(window, "editor-lua-before-reference.png");
        Click(window, Find<Button>(window, "InsertLuaAssetReference"));
        Assert.IsTrue(session.SelectedScript!.References.TryGetValue("scene", out var linked), Find<TextBlock>(window, "LuaAssetStatus").Text);
        Assert.AreEqual(sceneId, linked);
        Find<Expander>(window, "LuaReferencesSettings").IsExpanded = false; Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "AttachLuaScriptToScene"));
        var player = new NovellMaker.Core.Execution.ScenarioPlayer(session.Project!, sceneId, "ru").Settled();
        Assert.IsNull(player.State.Error, player.State.Error); Assert.AreEqual("yes", player.State.Variables["gui_script"]);
        Assert.AreEqual(id, ((NovellMaker.Core.Execution.RunScriptAction)session.Project!.Scenes[0].ScriptBlocks.Single().Action).ScriptId);
        Find<ScrollViewer>(window, "LuaScriptInspector").Offset = default; Dispatcher.UIThread.RunJobs();
        Capture(window, "editor-lua-library.png");
        session.OpenScene(sceneId); session.ActiveScene!.SelectedSection = 4; Dispatcher.UIThread.RunJobs();
        var headers = Find<TabControl>(window, "SceneSections").GetVisualDescendants().OfType<TabItem>().ToArray();
        Assert.AreEqual(7, headers.Length);
        foreach (var header in headers) Assert.IsTrue(header.Header is TextBlock { FontSize: 12 });
        var headerY = headers.Select(h => h.TranslatePoint(default, window)!.Value.Y).ToArray();
        Assert.IsTrue(headerY.All(y => Math.Abs(y - headerY[0]) < 1), "All six sections must share a single compact header row.");
        Assert.IsTrue(headers.All(h => h.Bounds.Height <= 36));
        Capture(window, "editor-compact-scene-tabs.png");
        var actions = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => !v.PropertiesOnly);
        Find<ListBox>(actions, "ScriptBlocks").ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Вызвать существующий скрипт"))
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        Assert.AreEqual(2, session.Project.Scenes[0].ScriptBlocks.Length);
        window.Close();
    });

    [TestMethod]
    public Task AudioTabImportsAndBindsVoiceAndMixerControls() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session); window.Show(); await session.OpenExampleAsync();
        var wave = Path.Combine(session.ProjectDirectory!, "voice-source.wav"); File.WriteAllBytes(wave, NovellMaker.Testing.AudioFixture.Wave());
        dialogs.AudioPath = wave;
        session.ActiveScene!.SelectedSection = 5; Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "ImportAudioButton"));
        for (var i = 0; i < 30 && session.IsBusy; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Assert.AreEqual(1, session.Project!.Audio.Assets.Length);
        var voice = Find<ComboBox>(window, "LineVoicePicker"); voice.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(session.Project.Audio.Assets[0].Id, session.Project.Audio.Voices[session.ActiveScene.SelectedLine!.Id]["ru"]);
        Find<ComboBox>(window, "SceneSoundPicker").SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(session.Project.Audio.Assets[0].Id, session.Project.Audio.SceneSounds[session.ActiveScene.Id]);
        Capture(window, "editor-audio.png");
        Find<NumericUpDown>(window, "SoundAudioVolume").Value = 40;
        Find<CheckBox>(window, "VoiceAudioMute").IsChecked = true; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(0.4f, session.Project.Audio.Mixer[NovellMaker.Core.Audio.AudioGroup.Sound].Volume);
        Assert.IsTrue(session.Project.Audio.Mixer[NovellMaker.Core.Audio.AudioGroup.Voice].Muted);
        Find<ScrollViewer>(window, "AudioScroll").Offset = new Vector(0, 1000); Dispatcher.UIThread.RunJobs();
        Capture(window, "editor-audio-mixer.png");
        window.Close(); session.Dispose();
    });

    [TestMethod]
    public Task WindowsInstallerDialogValidatesVersionAndCancelsWithoutSaving() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _);
        var window = CreateWindow(session); window.Show();
        await session.OpenExampleAsync(); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(OperatingSystem.IsWindows(), session.ExportWindowsInstallerCommand.CanExecute(null));
        Assert.IsNotNull(window.FindControl<MenuItem>("ExportWindowsInstallerMenu"));
        var result = new EditorDialogs(window).PickWindowsInstallerAsync(session.Project!);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        Assert.AreEqual(session.Project!.Scenes.Length, Find<ComboBox>(dialog, "InstallerStartScene").ItemCount);
        Find<TextBox>(dialog, "InstallerVersion").Text = "1.2";
        Click(dialog, Find<Button>(dialog, "ConfirmInstallerExport"));
        Assert.IsFalse(result.IsCompleted);
        StringAssert.Contains(Find<TextBlock>(dialog, "InstallerValidation").Text!, "три или четыре");
        Find<TextBox>(dialog, "InstallerVersion").Text = "1.2.3";
        Capture(dialog, "editor-windows-installer.png");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsNull(await result);
        window.Close(); session.Dispose();
    });

    [TestMethod]
    public Task WindowsExportDialogShowsStartSceneAndCancelsWithoutOpeningFolderPicker() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _);
        var window = CreateWindow(session); window.Show();
        await session.OpenExampleAsync(); Dispatcher.UIThread.RunJobs();
        var dialogs = new EditorDialogs(window);
        var result = dialogs.PickWindowsExportAsync(session.Project!);
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        var picker = Find<ComboBox>(dialog, "ExportStartScene");
        Assert.AreEqual(session.Project!.Scenes.Length, picker.ItemCount);
        StringAssert.Contains(picker.SelectedItem!.ToString()!, session.Project.Scenes[0].Name);
        Capture(dialog, "editor-windows-export.png");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsNull(await result);
        window.Close(); session.Dispose();
    });

    [TestMethod]
    public Task PhraseScriptBlocksApplyUndoSelectAndEditLuaWithNamedReferences() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session); window.Show(); Dispatcher.UIThread.RunJobs();
        Click(window, Find<Button>(window, "OpenExampleButton"));
        await UntilAsync(() => session.ActiveScene is not null && !session.IsBusy);
        Assert.AreEqual(4, session.ActiveScene!.SelectedSection);
        Assert.IsTrue(session.ExportWindowsCommand.CanExecute(null));
        Assert.IsNotNull(window.FindControl<MenuItem>("ExportWindowsMenu"));
        var inspector = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => !v.PropertiesOnly);
        var blocks = Find<ListBox>(inspector, "ScriptBlocks");
        Assert.AreEqual(2, blocks.ItemCount);
        blocks.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
        var before = session.Project!.Scenes[0].ScriptBlocks[1];
        var properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        var seconds = properties.GetVisualDescendants().OfType<NumericUpDown>().Last(); seconds.Value = 3;
        Click(window, Find<Button>(properties, "ApplyScriptBlock"));
        Assert.AreEqual(3, ((NovellMaker.Core.Execution.MoveAction)session.Project.Scenes[0].ScriptBlocks[1].Action).Seconds);
        session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(before, session.Project.Scenes[0].ScriptBlocks[1]);
        session.Redo(); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(3, ((NovellMaker.Core.Execution.MoveAction)session.Project.Scenes[0].ScriptBlocks[1].Action).Seconds);
        session.ActiveScene.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
        StringAssert.Contains(session.ActiveScene.SelectedPhraseScriptSummary, "Во время: 1");
        Click(window, Find<Button>(window, "OpenPhraseScriptButton"));
        Assert.AreEqual(0, session.ActiveScene.SelectedSection);
        properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        var phrases = Find<ComboBox>(properties, "ScriptPhrasePicker"); phrases.SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(session.Project.Scenes[0].Lines[2].Id, session.ActiveScene.SelectedLine!.Id);
        properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        Find<ListBox>(properties, "ScriptBlocks").SelectedIndex = 3; Dispatcher.UIThread.RunJobs();
        properties = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        Assert.IsFalse(inspector.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "LuaSourceBox"));
        var originalInline = (NovellMaker.Core.Execution.ScriptAction)session.Project.Scenes[0].ScriptBlocks.Select(b => b.Action).Single(a => a is NovellMaker.Core.Execution.ScriptAction s && s.Name == "terminal-result.lua");
        Click(window, Find<Button>(properties, "OpenInlineLuaExternally"));
        await UntilAsync(() => !session.IsBusy && dialogs.CodeEditor.Opened.Count > 0);
        Assert.AreEqual(originalInline.Source, session.SelectedScript!.Source);
        Assert.AreEqual(originalInline.References["correct"], session.SelectedScript.References["correct"]);
        session.InspectAction(originalInline.Id); Dispatcher.UIThread.RunJobs();
        inspector = window.GetVisualDescendants().OfType<ScenarioActionsView>().Single(v => v.PropertiesOnly);
        Capture(window, "editor-phrase-script.png");
        var count = session.Project.Scenes[0].ScriptBlocks.Length;
        var remove = inspector.ContextMenu!.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "Удалить"));
        remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); Assert.AreEqual(count, session.Project.Scenes[0].ScriptBlocks.Length);
        dialogs.AcceptDeletion = true; remove.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await UntilAsync(() => session.Project.Scenes[0].ScriptBlocks.Length == count - 1);
        session.Undo(); Assert.AreEqual(count, session.Project.Scenes[0].ScriptBlocks.Length);
        session.Dispose(); window.Hide();
    });

    [ThreadStatic] private static List<MainWindow>? windows;
    [TestMethod]
    public Task WindowStartsDarkWithNoDocumentAndSaveDisabled() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _);
        var window = CreateWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.AreEqual(ThemeVariant.Dark, window.ActualThemeVariant);
        Assert.IsFalse(window.FindControl<TabControl>("Documents")!.IsVisible);
        Assert.IsFalse(window.FindControl<Button>("SaveProjectButton")!.IsEffectivelyEnabled);
        Assert.IsTrue(window.FindControl<Button>("NewProjectButton")!.IsEffectivelyEnabled);
        Capture(window, "editor-empty.png");
        Assert.IsTrue(await session.RequestCloseAsync());
        window.Close();
    });

    [TestMethod]
    public Task NewButtonTypingAndSaveShortcutUpdateTheProjectAndDirtyMarker() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out var store);
        var window = CreateWindow(session);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var create = window.FindControl<Button>("NewProjectButton")!;
        var point = create.TranslatePoint(new Point(create.Bounds.Width / 2, create.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.HasProject, "The toolbar button must execute the create command.");
        Assert.IsTrue(window.FindControl<TabControl>("Documents")!.IsVisible);

        var name = Find<TextBox>(window, "ProjectNameBox");
        name.Focus();
        name.SelectAll();
        window.KeyTextInput("Лесные истории");
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual("Лесные истории", session.ProjectName);
        StringAssert.Contains(window.Title!, "*");

        window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(1, store.Saves);
        Assert.AreEqual(1, dialogs.SavePickerCalls);
        Assert.AreEqual("Лесные истории", store.Saved!.Name);
        Assert.IsFalse(session.IsDirty);
        Assert.IsFalse(window.Title!.Contains('*'));
        Capture(window, "editor-project.png");
        window.Close();
        await Task.CompletedTask;
    });

    [TestMethod]
    public Task ClosingWindowHonorsCancelAndDiscardDecisions() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        dialogs.Decision = UnsavedChangesDecision.Cancel;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.IsTrue(window.IsVisible, "Cancel must keep the unsaved project window open.");
        Assert.IsTrue(session.IsDirty);

        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.IsFalse(window.IsVisible);
        Assert.AreEqual(2, dialogs.ConfirmationCalls);
    });

    [TestMethod]
    public Task EmptyNameDisablesSaveAndShowsValidation() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out var store);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();
        var name = Find<TextBox>(window, "ProjectNameBox");
        Assert.IsTrue(name.Focus());
        name.SelectAll();
        window.KeyTextInput("   ");
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(window.FindControl<Button>("SaveProjectButton")!.IsEffectivelyEnabled);
        Assert.IsFalse(string.IsNullOrWhiteSpace(session.NameValidation));
        Assert.AreEqual(0, store.Saves);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    public Task SceneEditorHandlesTypingOneLineNavigationAndUndoThroughWindowInput() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        Dispatcher.UIThread.RunJobs();

        Click(window, Find<Button>(window, "AddSceneButton"));
        session.ActiveScene!.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
        Assert.IsNotNull(session.ActiveScene);
        var document = session.ActiveScene;
        Click(window, Find<Button>(window, "AddLineButton"));
        var firstId = document.SelectedLine!.Id;
        var text = Find<TextBox>(window, "LineTextBox");
        Assert.IsTrue(text.Focus());
        foreach (var character in "Первая реплика") window.KeyTextInput(character.ToString());
        Assert.AreEqual("Первая реплика", document.LineText);

        Click(window, Find<Button>(window, "AddLineButton"));
        var secondId = document.SelectedLine!.Id;
        text = Find<TextBox>(window, "LineTextBox");
        Assert.IsTrue(text.Focus());
        window.KeyTextInput("Вторая реплика");
        Assert.AreEqual("Вторая реплика", document.LineText);

        Click(window, Find<Button>(window, "PreviousLineButton"));
        Assert.AreEqual(firstId, document.SelectedLine!.Id);
        Assert.AreEqual("Первая реплика", Find<TextBox>(window, "LineTextBox").Text);
        Assert.IsFalse(Find<Button>(window, "PreviousLineButton").IsEffectivelyEnabled);
        Click(window, Find<Button>(window, "NextLineButton"));
        Assert.AreEqual(secondId, document.SelectedLine!.Id);
        Assert.IsFalse(Find<Button>(window, "NextLineButton").IsEffectivelyEnabled);

        text = Find<TextBox>(window, "LineTextBox");
        Assert.IsTrue(text.Focus());
        text.SelectAll();
        window.KeyTextInput("Исправленная реплика");
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Assert.AreEqual("Вторая реплика", document.LineText);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Assert.AreEqual("Исправленная реплика", document.LineText);
        Capture(window, "editor-scene.png");
        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    public Task DialogueSampleMapsCssAlphaAnimationAndVoiceVisibilityToRealControls() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync();
        session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        session.ActiveScene!.AddLine();
        session.ActiveScene.LineText = "Образец текста";
        session.ActiveScene.UseSceneStyle = true;
        session.ActiveScene.Css = """
            .dialogue {
              background-color: #20304080;
              color: #E0D0C0;
              width: 960px;
              left: 100px;
              bottom: 24px;
              padding: 20px;
              font-size: 26px;
              animation-name: appear;
              animation-duration: 2s;
              animation-timing-function: linear;
            }
            @keyframes appear {
              from { opacity: 0; transform: translateY(40px); }
              to { opacity: 1; transform: translateY(0px); }
            }
            """;
        Dispatcher.UIThread.RunJobs();
        var sample = Find<DialogueStyleSample>(window, "DialogueSample");
        var plate = Find<Border>(sample, "DialoguePlate");
        var body = Find<TextBlock>(sample, "DialogueText");
        Assert.AreEqual(Color.FromArgb(128, 32, 48, 64), ((SolidColorBrush)plate.Background!).Color);
        Assert.AreEqual(26, body.FontSize);
        Assert.AreEqual("Образец текста", body.Text);
        sample.SampleAt(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0.5, plate.Opacity, 0.00001);
        var transform = (TransformGroup)plate.RenderTransform!;
        Assert.AreEqual(20, ((TranslateTransform)transform.Children[1]).Y, 0.00001);
        Assert.IsTrue(Find<Button>(sample, "PlayStyleAnimationButton").IsEffectivelyEnabled);

        session.ShowVoiceProgress = false;
        session.ShowVoiceControls = false;
        Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(Find<StackPanel>(sample, "VoiceProgressPanel").IsVisible);
        Assert.IsFalse(Find<Button>(sample, "PauseVoiceButton").IsVisible);
        session.ShowVoiceControls = true;
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(Find<Button>(sample, "PauseVoiceButton").IsVisible);
        Assert.IsFalse(Find<Button>(sample, "PauseVoiceButton").IsEffectivelyEnabled,
            "No real voice backend is present; an authoring sample must not fake playback.");
        sample.SampleAt(TimeSpan.FromSeconds(2));
        Capture(window, "editor-dialogue-style.png");
        dialogs.Decision = UnsavedChangesDecision.Discard;
        window.Close();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    });

    [TestMethod]
    public Task ImageLibraryImportsRendersAndRenamesThenSceneBindsBackgroundWithUndo() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.UiImages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "Задник.png");
            using (var bitmap = new SKBitmap(640, 360))
            {
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(new SKColor(50, 69, 87));
                using var paint = new SKPaint { Color = new SKColor(86, 116, 123) };
                canvas.DrawRect(0, 220, 640, 140, paint);
                paint.Color = new SKColor(194, 185, 153);
                canvas.DrawCircle(480, 90, 36, paint);
                paint.Color = new SKColor(36, 55, 57);
                canvas.DrawRect(110, 100, 32, 210, paint);
                canvas.DrawCircle(126, 100, 65, paint);
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
                using var file = File.Create(source);
                encoded.SaveTo(file);
            }
            var dialogs = new UiDialogs { SavePath = Path.Combine(directory, "story.novell"), ImagePath = source };
            var session = new EditorSession(new JsonProjectStore(), dialogs, new UiGit());
            var window = CreateWindow(session);
            window.Show();
            await session.NewProjectAsync();
            Click(window, Find<Button>(window, "AssetLibraryButton"));
            Click(window, Find<Button>(window, "ImportImageButton"));
            await UntilAsync(() => session.IsIdle && session.Images.Count == 1);
            var asset = session.Images.Single();
            Assert.AreEqual(asset.Id, session.SelectedImage!.Id);
            var preview = Find<ProjectImageView>(window, "SelectedImagePreview");
            await UntilAsync(() => preview.HasImage && window.GetVisualDescendants().OfType<ProjectImageView>().Count(view => view.HasImage) >= 2);
            Assert.IsTrue(window.GetVisualDescendants().OfType<ProjectImageView>().Count(view => view.HasImage) >= 2,
                "Both the real thumbnail and selected full preview must render.");
            Capture(window, "editor-image-library.png");

            var name = Find<TextBox>(window, "SelectedImageNameBox");
            name.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(name.Focus());
            name.SelectAll();
            foreach (var character in "Вечерний парк") window.KeyTextInput(character.ToString());
            Assert.AreEqual("Вечерний парк", session.SelectedImageName);
            Assert.AreEqual(asset.Id, session.SelectedImage.Id);
            var filter = Find<TextBox>(window, "AssetFilterBox");
            Assert.IsTrue(filter.Focus());
            window.KeyTextInput("ПАРК");
            Assert.AreEqual(1, session.FilteredImages.Count);
            filter.SelectAll();
            window.KeyTextInput("ничего не найдено");
            Assert.AreEqual(0, session.FilteredImages.Count);
            session.AssetFilter = "";

            Click(window, Find<Button>(window, "AddSceneButton"));
        session.ActiveScene!.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
            var document = session.ActiveScene!;
            Find<TabControl>(window, "SceneSections").SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            var picker = Find<ComboBox>(window, "BackgroundPicker");
            picker.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(asset.Id, document.BackgroundImageId, "The picker must update the scene's stable ID binding.");
            preview = Find<ProjectImageView>(window, "BackgroundPreview");
            await UntilAsync(() => preview.HasImage);
            Assert.AreEqual(asset.Id, ((BackgroundImageOption)picker.SelectedItem!).Id,
                "After asynchronous rendering the picker must still display the assigned image.");
            Capture(window, "editor-background.png");
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.IsNull(document.BackgroundImageId);
            Assert.IsFalse(preview.HasImage);
            window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
            window.KeyRelease(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
            Assert.AreEqual(asset.Id, document.BackgroundImageId);
            await UntilAsync(() => preview.HasImage);

            File.Delete(AssetPath.Resolve(directory, asset.RelativePath));
            await session.RefreshGitAsync();
            await UntilAsync(() => !preview.HasImage && preview.PreviewStatus.Contains("Не удалось"));
            StringAssert.Contains(document.BackgroundStatus, "не найден");
            Assert.AreEqual(asset.Id, document.BackgroundImageId);
            dialogs.Decision = UnsavedChangesDecision.Discard;
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task PreviewDiscardsLateCompletionAfterSelectionChangesOrViewDetaches() => WithUi(async () =>
    {
        var service = new DelayedPreviewService();
        var first = new ImageAsset(Guid.NewGuid(), "first", "Assets/Images/first.png", 10, 20);
        var second = new ImageAsset(Guid.NewGuid(), "second", "Assets/Images/second.png", 30, 15);
        var view = new ProjectImageView(service) { ProjectDirectory = Path.GetTempPath(), Asset = first };
        var window = CreateWindow(CreateSession(out _, out _));
        window.Content = view;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var firstRequest = service.Requests.Single();
        var firstLoad = view.PreviewLoad;
        view.Asset = second;
        var secondRequest = service.Requests.Last();
        Assert.IsTrue(firstRequest.Cancellation.IsCancellationRequested);
        secondRequest.Completion.SetResult(PreviewPng(30, 15));
        await UntilAsync(() => view.HasImage);
        var image = view.GetVisualDescendants().OfType<Image>().Single();
        Assert.AreEqual(new PixelSize(30, 15), ((Bitmap)image.Source!).PixelSize);
        var accepted = image.Source;

        // A backend may finish native decoding despite cancellation. Its old result must be discarded.
        firstRequest.Completion.SetResult(PreviewPng(10, 20));
        await firstLoad;
        Assert.AreSame(accepted, image.Source);

        view.Asset = first;
        var detachedRequest = service.Requests.Last();
        var detachedLoad = view.PreviewLoad;
        window.Content = null;
        Assert.IsTrue(detachedRequest.Cancellation.IsCancellationRequested);
        Assert.IsFalse(view.HasImage);
        Assert.IsNull(image.Source);
        detachedRequest.Completion.SetResult(PreviewPng(10, 20));
        await detachedLoad;
        Assert.IsFalse(view.HasImage);
        Assert.IsNull(image.Source);
        window.Content = view;
        service.Requests.Last().Completion.SetResult(PreviewPng(10, 20));
        await UntilAsync(() => view.HasImage);
        Assert.AreEqual(new PixelSize(10, 20), ((Bitmap)image.Source!).PixelSize);
        window.Close();
    });

    [TestMethod]
    public Task CharacterLibraryAndLinePickersRenderSpeakerStateColorsAndAssignedBackground() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker.UiCharacters", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var background = Path.Combine(directory, "park.png");
            var normal = Path.Combine(directory, "normal.png");
            var sad = Path.Combine(directory, "sad.png");
            File.WriteAllBytes(background, PreviewPng(640, 360));
            File.WriteAllBytes(normal, SpritePng(SKColors.Coral));
            File.WriteAllBytes(sad, SpritePng(SKColors.MediumPurple));
            var dialogs = new UiDialogs { SavePath = Path.Combine(directory, "story.novell") };
            var previewService = new RecordingPreview();
            var session = new EditorSession(new JsonProjectStore(), dialogs, new UiGit(), previewService: previewService);
            var window = CreateWindow(session);
            window.Width = 1600;
            window.Height = 1000;
            window.Show();
            await session.NewProjectAsync();
            foreach (var file in new[] { background, normal, sad }) { dialogs.ImagePath = file; await session.ImportImageAsync(); }
            Find<TreeView>(window, "ProjectExplorer").SelectedItem = session.Explorer[0].Children.Single(entry => entry.IsCharacterLibrary);
            Dispatcher.UIThread.RunJobs();
            Click(window, Find<Button>(window, "AddCharacterButton"));
            TypeInto(window, Find<TextBox>(window, "CharacterNameBox"), "Анна");
            Click(window, Find<Button>(window, "AddCharacterStateButton"));
            var normalId = session.SelectedState!.Id;
            TypeInto(window, Find<TextBox>(window, "CharacterStateNameBox"), "normal");
            Find<ComboBox>(window, "CharacterStateImageBox").SelectedItem = session.Images[1];
            Dispatcher.UIThread.RunJobs();
            Click(window, Find<Button>(window, "AddCharacterStateButton"));
            var sadId = session.SelectedState!.Id;
            TypeInto(window, Find<TextBox>(window, "CharacterStateNameBox"), "sad");
            Find<ComboBox>(window, "CharacterStateImageBox").SelectedItem = session.Images[2];
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(normalId, session.SelectedCharacter!.DefaultStateId);
            await UntilAsync(() => Find<ProjectImageView>(window, "CharacterStatePreview").HasImage);
            Capture(window, "editor-characters.png");

            Click(window, Find<Button>(window, "AddSceneButton"));
        session.ActiveScene!.SelectedSection = 0; Dispatcher.UIThread.RunJobs();
            Click(window, Find<Button>(window, "AddLineButton"));
            var document = session.ActiveScene!;
            var lineId = document.SelectedLine!.Id;
            document.BackgroundImageId = session.Images[0].Id;
            Find<ComboBox>(window, "SpeakerPicker").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(normalId, document.SampleFrame!.Characters.Single().StateId);
            Find<ComboBox>(window, "CharacterStatePicker").SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            TypeInto(window, Find<TextBox>(window, "LineTextBox"), "Привет, §#E08080я грущу§r.");
            var sample = Find<DialogueStyleSample>(window, "DialogueSample");
            sample.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var sceneBackground = Find<ProjectImageView>(sample, "SceneBackground");
            var layer = Find<Canvas>(sample, "CharacterLayer");
            await UntilAsync(() => sceneBackground.HasImage && layer.Children.OfType<ProjectImageView>().Single().HasImage);
            Assert.AreEqual(sadId, document.SampleFrame!.Characters.Single().StateId);
            Assert.AreEqual("Анна", Find<TextBlock>(sample, "DialogueSpeaker").Text);
            var body = Find<TextBlock>(sample, "DialogueText");
            var colored = body.Inlines!.OfType<Run>().Single(run => run.Text == "я грущу");
            Assert.AreEqual(Color.FromRgb(224, 128, 128), ((SolidColorBrush)colored.Foreground!).Color);
            Assert.IsTrue(layer.Children.OfType<ProjectImageView>().Single().TransparentBackground);
            Capture(window, "editor-scene-character.png");

            Find<TabControl>(window, "SceneSections").SelectedItem = Find<TabItem>(window, "PlacementTab");
            Dispatcher.UIThread.RunJobs();
            sample = Find<DialogueStyleSample>(window, "PlacementSample");
            layer = Find<Canvas>(sample, "CharacterLayer");
            var xBox = Find<NumericUpDown>(window, "PlacementXBox");
            xBox.Value = 25;
            Find<NumericUpDown>(window, "PlacementYBox").Value = 85;
            Find<NumericUpDown>(window, "PlacementScaleBox").Value = 75;
            Find<NumericUpDown>(window, "PlacementLayerBox").Value = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(25m, document.PlacementX);
            Assert.AreEqual(85m, document.PlacementY);
            Assert.AreEqual(75m, document.PlacementScale);
            Assert.AreEqual(4m, document.PlacementLayer);
            sample.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            var sprite = layer.Children.OfType<ProjectImageView>().Single();
            Assert.AreEqual(320, Canvas.GetLeft(sprite) + sprite.Width / 2, 0.01);
            Assert.AreEqual(612, Canvas.GetTop(sprite) + sprite.Height, 0.01);
            var beforeDrag = document.SelectedLine!.Model.Placements.Single().Placement!;
            var start = sprite.TranslatePoint(new Point(sprite.Width / 2, 60), window)!.Value;
            var end = layer.TranslatePoint(new Point(450, Canvas.GetTop(sprite) + 90), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end);
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var afterDrag = document.SelectedLine!.Model.Placements.Single().Placement!;
            Assert.AreNotEqual(beforeDrag.X, afterDrag.X, "Dragging the rendered sprite must write a placement command.");
            Assert.AreEqual(450, afterDrag.X * 1280, 2);
            Assert.AreEqual(beforeDrag.Y * 720 + 30, afterDrag.Y * 720, 2);
            Assert.AreEqual(beforeDrag.Scale, afterDrag.Scale);
            session.Undo();
            Assert.AreEqual(beforeDrag, document.SelectedLine.Model.Placements.Single().Placement, "A complete drag is one undo operation.");
            session.Redo();
            Assert.AreEqual(afterDrag, document.SelectedLine.Model.Placements.Single().Placement);
            // Escape cancels a live drag without writing another command.
            var cancelStart = sprite.TranslatePoint(new Point(sprite.Width / 2, 60), window)!.Value;
            window.MouseDown(cancelStart, MouseButton.Left);
            window.MouseMove(cancelStart + new Point(30, 0));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(cancelStart + new Point(30, 0), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(afterDrag, document.SelectedLine.Model.Placements.Single().Placement);
            Assert.AreEqual(afterDrag.X * 1280, Canvas.GetLeft(sprite) + sprite.Width / 2, 0.01);
            Capture(window, "editor-placement.png");
            Find<TabControl>(window, "SceneSections").SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();


            Click(window, Find<Button>(window, "PreviewSceneButton"));
            await UntilAsync(() => previewService.Request is not null && session.IsIdle);
            Assert.AreEqual(lineId, previewService.Request!.LineId);
            Assert.AreEqual(document.Id, previewService.Request.SceneId);
            Assert.AreEqual(directory, previewService.Request.AssetsRoot);
            Assert.IsTrue(session.IsDirty, "Launching preview must use the current draft without implicitly saving it.");
            Assert.AreEqual(0, (await new JsonProjectStore().LoadAsync(dialogs.SavePath)).Scenes.Length);
            var captured = previewService.Request.Project;
            document.LineText = "Позже изменённый текст";
            Assert.AreEqual("Привет, §#E08080я грущу§r.", captured.Scenes.Single().Lines.Single().Texts["ru"]);
            Click(window, Find<Button>(window, "AddLineButton"));
            Find<ComboBox>(window, "HideCharacterPicker").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(0, document.SampleFrame!.Characters.Length);
            Assert.AreEqual("", Find<TextBlock>(Find<DialogueStyleSample>(window, "DialogueSample"), "DialogueSpeaker").Text);
            document.Navigate(-1);
            Assert.AreEqual(sadId, document.SampleFrame!.Characters.Single().StateId);
            dialogs.Decision = UnsavedChangesDecision.Discard;
            window.Close();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.IsTrue(previewService.Disposed);
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task WorkspaceSupportsMouseDockResizeHideRestoreAndKeepsDocumentState() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session);
        window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        session.ActiveScene!.AddLine();
        session.ActiveScene.LineText = "Текст сохраняется при перестройке панелей";
        Dispatcher.UIThread.RunJobs();
        var scene = window.FindControl<ContentControl>("ScenePane")!;
        var docs = window.FindControl<Grid>("DocumentsPane")!;
        var sample = Find<DialogueStyleSample>(window, "DialogueSample");
        var layout = window.WorkspaceLayout;
        Find<TabControl>(window, "SceneSections").SelectedIndex = 2;
        var initialWidth = docs.Bounds.Width;
        layout.Hide("explorer"); Dispatcher.UIThread.RunJobs();
        Assert.IsGreaterThan(initialWidth, docs.Bounds.Width);
        layout.Hide("information"); Dispatcher.UIThread.RunJobs();
        var frame = scene.GetVisualAncestors().OfType<Border>().First();
        var header = frame.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Экран сцены");
        var start = header.TranslatePoint(new Point(12, 10), window)!.Value;
        var target = docs.TranslatePoint(new Point(docs.Bounds.Width - 15, docs.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left); window.MouseMove(target); window.MouseUp(target, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var sceneOrigin = scene.TranslatePoint(default, window)!.Value;
        var docsOrigin = docs.TranslatePoint(default, window)!.Value;
        Assert.IsGreaterThan(docsOrigin.X, sceneOrigin.X, "Scene must dock to the right of documents.");
        Assert.AreEqual(docsOrigin.Y, sceneOrigin.Y, 3);
        Assert.AreSame(session.ActiveScene, Find<DialogueStyleSample>(window, "DialogueSample").PlacementEditor);
        Assert.AreEqual("Текст сохраняется при перестройке панелей", session.ActiveScene.LineText);
        Assert.AreEqual(2, Find<TabControl>(window, "SceneSections").SelectedIndex, "Docking must preserve the active editor section.");
        Find<TabControl>(window, "SceneSections").SelectedIndex = 0;
        var split = scene.GetVisualAncestors().OfType<Grid>().First(g => g.Children.OfType<GridSplitter>().Any());
        var splitter = split.Children.OfType<GridSplitter>().Single();
        var divider = splitter.TranslatePoint(new Point(2, splitter.Bounds.Height / 2), window)!.Value;
        var beforeResize = docs.Bounds.Width;
        window.MouseDown(divider, MouseButton.Left); window.MouseMove(divider + new Point(60, 0));
        window.MouseUp(divider + new Point(60, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.IsGreaterThan(beforeResize, docs.Bounds.Width);
        var savedLayout = layout.ExportLayout();
        layout.Reset(); layout.ImportLayout(savedLayout); Dispatcher.UIThread.RunJobs();
        Assert.IsFalse(layout.IsVisible("explorer"));
        Assert.AreEqual(beforeResize + 60, docs.Bounds.Width, 4);
        Capture(window, "workspace-docked.png");
        var stableLayout = layout.ExportLayout();
        Assert.ThrowsExactly<System.IO.InvalidDataException>(() => layout.ImportLayout("{\"Pane\":\"unknown\"}"));
        Assert.AreEqual(stableLayout, layout.ExportLayout());
        header = scene.GetVisualAncestors().OfType<Border>().First().GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "Экран сцены");
        start = header.TranslatePoint(new Point(12, 10), window)!.Value;
        target = docs.TranslatePoint(new Point(10, 10), window)!.Value;
        window.MouseDown(start, MouseButton.Left); window.MouseMove(target);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(target, MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(stableLayout, layout.ExportLayout(), "Escape must cancel docking.");
        foreach (var id in new[] { "documents", "scene", "messages", "explorer", "information" }) layout.Hide(id);
        Dispatcher.UIThread.RunJobs();
        layout.Show("scene"); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(layout.IsVisible("scene"));
        Assert.AreSame(session.ActiveScene, Find<DialogueStyleSample>(window, "DialogueSample").PlacementEditor);
        layout.Reset(); Dispatcher.UIThread.RunJobs();
        Capture(window, "workspace-default.png");
        window.Close();
    });

    [TestMethod]
    public Task ExplorerModePickerChangesActualFilesAndParticipatesInUndo() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "novell-explorer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "Assets", "Images"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Assets", "Images", "readme.txt"), "ресурсы");
        try
        {
            var session = CreateSession(out var dialogs, out _);
            dialogs.SavePath = Path.Combine(directory, "test-ui.novell");
            dialogs.Decision = UnsavedChangesDecision.Discard;
            var window = CreateWindow(session); window.Show();
            await session.NewProjectAsync(); await session.SaveAsync();
            Dispatcher.UIThread.RunJobs();
            var picker = Find<ComboBox>(window, "ExplorerModePicker");
            picker.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(session.Project!.ExplorerShowsFiles);
            Assert.IsTrue(session.IsDirty);
            var assets = session.Explorer[0].Items.Single(item => item.Label == "Assets/");
            var images = assets.Items.Single(item => item.Label == "Images/");
            Assert.AreEqual("readme.txt", images.Items.Single().Label);
            Assert.IsFalse(assets.ExpandInitially);
            var tree = Find<TreeView>(window, "FolderNavigation");
            Assert.IsFalse(window.FindControl<TreeView>("ProjectExplorer")!.IsVisible);
            var folder = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => ReferenceEquals(item.DataContext, assets));
            folder.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            var imageFolder = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => ReferenceEquals(item.DataContext, images));
            tree.SelectedItem = images; Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(Find<ListBox>(window, "FolderTiles").GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "readme.txt"));
            await File.WriteAllTextAsync(Path.Combine(directory, "added.txt"), "обновление");
            await session.RefreshGitAsync(); Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(session.Explorer[0].Items.Any(item => item.Label == "added.txt"));
            session.Undo(); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(0, picker.SelectedIndex);
            Assert.IsFalse(session.IsDirty);
            session.Redo(); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(1, picker.SelectedIndex);
            await session.SaveAsync();
            Capture(window, "workspace-files.png");
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task ScenePanelRemainsVisibleWhenMovedLeftTopBottomAndBack() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0; session.ActiveScene!.AddLine();
        session.ActiveScene.LineText = "Сцена остаётся видимой после переноса";
        var layout = window.WorkspaceLayout;
        foreach (var id in new[] { "information", "explorer", "messages" }) layout.Hide(id);
        Dispatcher.UIThread.RunJobs();
        var scene = window.FindControl<ContentControl>("ScenePane")!;
        var docs = window.FindControl<Grid>("DocumentsPane")!;
        foreach (var side in new[] { Dock.Left, Dock.Top, Dock.Bottom, Dock.Right, Dock.Left })
        {
            layout.Move("scene", "documents", side); Dispatcher.UIThread.RunJobs();
            var a = new Rect(scene.TranslatePoint(default, window)!.Value, scene.Bounds.Size);
            var b = new Rect(docs.TranslatePoint(default, window)!.Value, docs.Bounds.Size);
            Assert.IsFalse(a.Intersects(b), $"Panels must not overlap after docking {side}.");
            Assert.IsGreaterThan(100d, a.Width); Assert.IsGreaterThan(50d, a.Height);
            var sample = Find<DialogueStyleSample>(window, "DialogueSample");
            var point = sample.TranslatePoint(new Point(sample.Bounds.Width / 2, sample.Bounds.Height / 2), window)!.Value;
            var visible = window.GetVisualAt(point);
            Assert.IsTrue(visible is not null && visible.GetVisualAncestors().Contains(scene),
                $"Scene preview must be visible and not covered by documents after docking {side}.");
            Assert.AreEqual(session.ActiveScene.LineText, Find<TextBlock>(sample, "DialogueText").Text);
        }
        Capture(window, "workspace-scene-left.png");
        var saved = layout.ExportLayout(); layout.Reset(); layout.ImportLayout(saved);
        Dispatcher.UIThread.RunJobs();
        Assert.IsLessThan(docs.TranslatePoint(default, window)!.Value.X, scene.TranslatePoint(default, window)!.Value.X);
        layout.Hide("documents"); Dispatcher.UIThread.RunJobs();
        var host = window.FindControl<Grid>("Workspace")!;
        Assert.AreEqual(host.Bounds.Width - 2, scene.Bounds.Width, 2, "A panel promoted to root must fill the workspace.");
        window.Close();
    });

    [TestMethod]
    public Task FolderExplorerOpensScenesAndReflectsRenameAndUndo() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _);
        dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        var first = session.ActiveScene!; first.Name = "Вступление";
        session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        session.ExplorerModeIndex = 1;
        Dispatcher.UIThread.RunJobs();
        var tree = Find<TreeView>(window, "FolderNavigation");
        var browser = window.FindControl<ProjectFolderView>("ProjectFolders")!.Browser!;
        var scenes = session.Explorer[0].Items.Single(entry => entry.Label == "Сцены/");
        Assert.AreEqual(2, scenes.Items.Count);
        var folder = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => ReferenceEquals(item.DataContext, scenes));
        folder.IsExpanded = true; Dispatcher.UIThread.RunJobs();
        var firstFolder = scenes.Items.Single(item => item.SceneId == first.Id);
        Assert.IsTrue(firstFolder.IsFolder);
        tree.SelectedItem = firstFolder; Dispatcher.UIThread.RunJobs();
        Assert.AreNotEqual(first.Id, session.ActiveScene!.Id, "Browsing folders must not change the active document.");
        var tiles = Find<ListBox>(window, "FolderTiles");
        Assert.AreEqual(first.Id, browser.CurrentFolder!.SceneId);
        Assert.AreEqual(first.Id, browser.Tiles.Single().Entry.SceneId);
        tiles.SelectedItem = browser.Tiles.Single(); Dispatcher.UIThread.RunJobs(); Assert.IsTrue(tiles.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus());
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(first.Id, session.ActiveScene!.Id);
        Assert.AreEqual(first.Id, firstFolder.Items.Single().SceneId);
        session.ActiveScene.Name = "Новый пролог"; Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.Explorer[0].Items.Single(item => item.Label == "Сцены/").Items.Any(item => item.Label == "Новый пролог/"));
        session.Undo(); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(session.Explorer[0].Items.Single(item => item.Label == "Сцены/").Items.Any(item => item.Label == "Вступление/"));
        Assert.IsTrue(tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext is ExplorerEntry { Label: "Сцены/" }).IsExpanded,
            "Editing a scene must keep its project folder expanded.");
        Capture(window, "workspace-project-folders.png");
        window.Close();
    });

    [TestMethod]
    public Task FolderTilesNavigateSearchPreviewImagesAndKeepLocationAfterRefresh() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "novell-tiles-" + Guid.NewGuid().ToString("N"));
        var projectDirectory = Path.Combine(directory, "Novel");
        Directory.CreateDirectory(projectDirectory);
        foreach (var name in new[] { "Audio", "Backgrounds", "Scripts", "Sprites", "Fonts", "Video" }) Directory.CreateDirectory(Path.Combine(projectDirectory, name));
        try
        {
            var session = CreateSession(out var dialogs, out _);
            dialogs.Decision = UnsavedChangesDecision.Discard;
            dialogs.SavePath = Path.Combine(projectDirectory, "project.novell");
            var window = CreateWindow(session); window.Show();
            await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0; session.ActiveScene!.Name = "Вступление";
            await session.SaveAsync();
            var source = Path.Combine(directory, "park.png"); await File.WriteAllBytesAsync(source, PreviewPng(160, 90));
            dialogs.ImagePath = source; await session.ImportImageAsync();
            session.ExplorerModeIndex = 1;
            var activeDocument = session.SelectedDocument;
            window.WorkspaceLayout.Hide("information"); window.WorkspaceLayout.Hide("scene"); window.WorkspaceLayout.Hide("messages");
            window.WorkspaceLayout.Move("explorer", "documents", Dock.Bottom);
            Dispatcher.UIThread.RunJobs();
            var view = window.FindControl<ProjectFolderView>("ProjectFolders")!;
            var browser = view.Browser!;
            var tiles = Find<ListBox>(view, "FolderTiles");
            Assert.IsTrue(tiles.ItemsPanelRoot is WrapPanel);
            Assert.IsGreaterThan(5, browser.Tiles.Count);
            Capture(window, "folder-browser-tiles.png");
            var assets = tiles.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is FolderTile { Name: "Assets" });
            var point = assets.TranslatePoint(new Point(30, 30), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Assets/", browser.CurrentFolder!.Label, "A double-click must enter the folder.");
            Assert.AreSame(activeDocument, session.SelectedDocument);
            browser.Open(browser.Tiles.Single(tile => tile.Name == "Images").Entry);
            Dispatcher.UIThread.RunJobs();
            var thumbnail = tiles.GetVisualDescendants().OfType<ProjectImageView>().Single(image => image.IsVisible);
            await UntilAsync(() => thumbnail.HasImage);
            Assert.IsTrue(browser.Tiles.Single().HasImage);
            var search = Find<TextBox>(view, "FolderSearchBox");
            TypeInto(window, search, "missing"); Assert.IsTrue(browser.IsEmpty);
            TypeInto(window, search, "park"); Assert.AreEqual(1, browser.Tiles.Count);
            TypeInto(window, search, "");
            await session.RefreshGitAsync(); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual("Images/", browser.CurrentFolder!.Label);
            Assert.IsTrue(browser.Tiles.Single().HasImage);
            Click(window, Find<Button>(view, "FolderBackButton"));
            Assert.AreEqual("Assets/", browser.CurrentFolder!.Label);
            browser.GoForward(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual("Images/", browser.CurrentFolder!.Label);
            Capture(window, "folder-browser-thumbnail.png");
            Click(window, Find<Button>(view, "FolderUpButton"));
            Assert.AreEqual("Assets/", browser.CurrentFolder!.Label);
            var rootCrumb = view.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is FolderBreadcrumb { Depth: 0 });
            Click(window, rootCrumb);
            Assert.AreEqual(session.ProjectName, browser.CurrentFolder!.Label);
            var address = browser.CurrentPath;
            window.WorkspaceLayout.Move("explorer", "documents", Dock.Left); Dispatcher.UIThread.RunJobs();
            Assert.AreEqual(address, browser.CurrentPath);
            session.ExplorerModeIndex = 0; Dispatcher.UIThread.RunJobs();
            Assert.IsTrue(window.FindControl<TreeView>("ProjectExplorer")!.IsVisible);
            Assert.IsFalse(view.IsVisible);
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    });

    [TestMethod]
    public Task DeleteKeyInHierarchyAndTilesRequiresConfirmationAndPreservesTextEditing() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show();
        await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        var id = session.ActiveScene!.Id;
        Dispatcher.UIThread.RunJobs();
        var tree = window.FindControl<TreeView>("ProjectExplorer")!;
        tree.SelectedItem = session.Explorer[0].Children.Single(e => e.Label.StartsWith("Сцены")).Children.Single();
        Dispatcher.UIThread.RunJobs();
        var row = tree.GetVisualDescendants().OfType<TreeViewItem>().Single(item => item.DataContext is ExplorerEntry entry && entry.SceneId == id);
        Assert.IsTrue(row.Focus());
        PressDelete(window); Assert.AreEqual(1, dialogs.Deletions.Count); Assert.AreEqual(1, session.Project!.Scenes.Length);
        dialogs.AcceptDeletion = true; PressDelete(window); await UntilAsync(() => !session.IsBusy);
        Assert.AreEqual(0, session.Project.Scenes.Length);
        session.Undo(); session.ExplorerModeIndex = 1; Dispatcher.UIThread.RunJobs();
        var browserView = window.FindControl<ProjectFolderView>("ProjectFolders")!;
        var browser = browserView.Browser!;
        browser.Open(browser.Tiles.Single(tile => tile.Name == "Сцены").Entry); Dispatcher.UIThread.RunJobs();
        var tiles = Find<ListBox>(browserView, "FolderTiles");
        tiles.SelectedItem = browser.Tiles.Single(); Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(tiles.GetVisualDescendants().OfType<ListBoxItem>().Single().Focus());
        dialogs.AcceptDeletion = false; PressDelete(window);
        Assert.AreEqual(3, dialogs.Deletions.Count); Assert.AreEqual(1, session.Project.Scenes.Length);
        dialogs.AcceptDeletion = true; PressDelete(window); await UntilAsync(() => !session.IsBusy);
        Assert.AreEqual(0, session.Project.Scenes.Length); Assert.IsTrue(browser.IsEmpty);
        var search = Find<TextBox>(browserView, "FolderSearchBox");
        TypeInto(window, search, "abc"); search.SelectionStart = 0; search.SelectionEnd = 0; search.CaretIndex = 0;
        PressDelete(window);
        Assert.AreEqual("bc", search.Text); Assert.AreEqual(4, dialogs.Deletions.Count);
        window.Close();
    });

    [TestMethod]
    public Task SceneAndLineDeleteButtonsConfirmBeforeChangingTheProject() => WithUi(async () =>
    {
        var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard;
        var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); session.AddScene(); session.ActiveScene!.SelectedSection = 0;
        var document = session.ActiveScene!; document.AddLine(); document.LineText = "Не удалять без подтверждения";
        Dispatcher.UIThread.RunJobs();
        var lineButton = window.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.Command, document.DeleteLineCommand));
        Click(window, lineButton); Assert.AreEqual(1, document.Lines.Count); Assert.AreEqual(1, dialogs.Deletions.Count);
        dialogs.AcceptDeletion = true; Click(window, lineButton); Assert.AreEqual(0, document.Lines.Count);
        session.Undo(); Assert.AreEqual("Не удалять без подтверждения", session.ActiveScene!.LineText);
        dialogs.AcceptDeletion = false;
        var sceneButton = window.GetVisualDescendants().OfType<Button>().Single(button => button.Content is "Удалить сцену");
        Click(window, sceneButton); Assert.AreEqual(1, session.Project!.Scenes.Length);
        dialogs.AcceptDeletion = true; Click(window, sceneButton); Assert.AreEqual(0, session.Project.Scenes.Length);
        Assert.AreEqual(4, dialogs.Deletions.Count); window.Close();
    });

    [TestMethod]
    public Task ActualDeleteDialogCancelsWithEscapeAndAcceptsOnlyExplicitDelete() => WithUi(async () =>
    {
        var session = CreateSession(out _, out _); var window = CreateWindow(session); window.Show();
        var dialogs = new EditorDialogs(window);
        var result = dialogs.ConfirmDeleteAsync("Сцена 1", "Будут удалены все реплики сцены. Отмена: Ctrl+Z.");
        Dispatcher.UIThread.RunJobs();
        var dialog = window.OwnedWindows.Single();
        Assert.IsTrue(Find<Button>(dialog, "CancelDeleteButton").IsFocused);
        Capture(dialog, "confirm-deletion.png");
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.IsFalse(await result);
        result = dialogs.ConfirmDeleteAsync("Сцена 1", "Будут удалены все реплики сцены.");
        Dispatcher.UIThread.RunJobs(); dialog = window.OwnedWindows.Single();
        Click(dialog, Find<Button>(dialog, "ConfirmDeleteButton"));
        Assert.IsTrue(await result); window.Close();
    });

    [TestMethod]
    public Task CharacterStateAndImageButtonsUseTheSameConfirmationAndUndo() => WithUi(async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "novell-delete-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var session = CreateSession(out var dialogs, out _); dialogs.Decision = UnsavedChangesDecision.Discard;
            dialogs.SavePath = Path.Combine(directory, "project.novell");
            var source = Path.Combine(directory, "source.png"); await File.WriteAllBytesAsync(source, PreviewPng(32, 32));
            dialogs.ImagePath = source;
            var window = CreateWindow(session); window.Show(); await session.NewProjectAsync(); await session.ImportImageAsync();
            var file = Path.Combine(directory, session.Project!.Images.Single().RelativePath);
            session.AddCharacter(); session.AddCharacterState(); Dispatcher.UIThread.RunJobs();
            var stateId = session.SelectedState!.Id;
            var stateButton = Find<Button>(window, "DeleteCharacterStateButton");
            Click(window, stateButton); Assert.AreEqual(stateId, session.SelectedState!.Id);
            dialogs.AcceptDeletion = true; Click(window, stateButton); Assert.IsTrue(session.Project.Characters[0].States.IsEmpty);
            session.Undo(); Dispatcher.UIThread.RunJobs(); Assert.AreEqual(stateId, session.SelectedState!.Id);
            var characterButton = Find<Button>(window, "DeleteCharacterButton");
            dialogs.AcceptDeletion = false; Click(window, characterButton); Assert.AreEqual(1, session.Project.Characters.Length);
            dialogs.AcceptDeletion = true; Click(window, characterButton); Assert.IsTrue(session.Project.Characters.IsEmpty);
            session.Undo(); session.SelectImage(session.Project.Images.Single().Id); Dispatcher.UIThread.RunJobs();
            var imageButton = Find<Button>(window, "DeleteImageButton");
            dialogs.AcceptDeletion = false; Click(window, imageButton); Assert.IsTrue(File.Exists(file));
            dialogs.AcceptDeletion = true; Click(window, imageButton); await UntilAsync(() => !session.IsBusy);
            Assert.IsFalse(File.Exists(file)); Assert.IsTrue(session.Project.Images.IsEmpty);
            session.Undo(); Assert.IsTrue(File.Exists(file)); Assert.AreEqual(1, session.Project.Images.Length);
            Assert.AreEqual(6, dialogs.Deletions.Count);
            window.Close();
        }
        finally { Directory.Delete(directory, true); }
    });

    private static void PressDelete(Window window)
    {
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void TypeInto(Window window, TextBox box, string value)
    {
        box.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.IsTrue(box.Focus());
        box.SelectAll();
        window.KeyTextInput(value);
        Dispatcher.UIThread.RunJobs();
    }

    private static byte[] SpritePng(SKColor color)
    {
        using var bitmap = new SKBitmap(80, 180);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = color };
        canvas.DrawCircle(40, 35, 25, paint);
        canvas.DrawRect(15, 60, 50, 110, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class RecordingPreview : IPreviewService
    {
        public GamePreviewRequest? Request { get; private set; }
        public bool Disposed { get; private set; }
        public Task StartAsync(GamePreviewRequest request, Action<string> report) { Request = request; return Task.CompletedTask; }
        public void Dispose() => Disposed = true;
    }

    private static byte[] PreviewPng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SlateBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private sealed class DelayedPreviewService : IImageAssetService
    {
        public List<PreviewRequest> Requests { get; } = [];
        public Task<ImageAsset> ImportAsync(string directory, string source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CopyAsync(string source, string target, IReadOnlyList<ImageAsset> images, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<byte[]> ReadPreviewAsync(string directory, ImageAsset asset, int maximumWidth = 1280, CancellationToken cancellationToken = default)
        {
            var request = new PreviewRequest(cancellationToken, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Requests.Add(request);
            return request.Completion.Task;
        }
    }

    private sealed record PreviewRequest(CancellationToken Cancellation, TaskCompletionSource<byte[]> Completion);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.IsTrue(condition(), "Asynchronous UI operation did not reach its expected state.");
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Click(Window window, Control control)
    {
        control.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WithUi(Func<Task> action)
    {
        await using var headless = HeadlessUnitTestSession.StartNew(typeof(UiAppBuilder));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await headless.Dispatch(async () =>
        {
            windows = [];
            try { await action(); return true; }
            finally
            {
                foreach (var window in windows) window.Hide();
                windows = null;
            }
        }, timeout.Token);
    }

    private static MainWindow CreateWindow(EditorSession session)
    {
        var window = new MainWindow(session);
        windows!.Add(window);
        return window;
    }

    private static void Capture(Window window, string fileName)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.IsNotNull(frame, "The actual editor must render successfully.");
        Assert.IsGreaterThan(0, frame.PixelSize.Width);
        var directory = Environment.GetEnvironmentVariable("NOVELLMAKER_CAPTURE_DIR");
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            frame.Save(Path.Combine(directory, fileName), PngBitmapEncoderOptions.Default);
        }
    }

    private static EditorSession CreateSession(out UiDialogs dialogs, out UiStore store)
    {
        dialogs = new UiDialogs();
        store = new UiStore();
        return new(store, dialogs, new UiGit(), codeEditor: dialogs.CodeEditor);
    }

    private sealed class UiDialogs : IEditorDialogs
    {
        public Guid? ScriptChoice { get; set; }
        public bool CancelScriptChoice { get; set; }
        public Task<Guid?> PickScriptAsync(IReadOnlyList<NovellMaker.Core.Execution.LuaScriptAsset> scripts) => Task.FromResult(CancelScriptChoice ? null : ScriptChoice ?? scripts.FirstOrDefault()?.Id);
        public UiCodeEditor CodeEditor { get; } = new();
        public Task<string?> PickCodeEditorAsync() => Task.FromResult<string?>("C:/Fixture/FixtureEditor.exe");
        public int SavePickerCalls { get; private set; }
        public int ConfirmationCalls { get; private set; }
        public string SavePath { get; set; } = Path.Combine(Path.GetTempPath(), "test-ui.novell");
        public string? AudioPath { get; set; }
        public Task<string?> PickAudioAsync() => Task.FromResult(AudioPath);
        public string? ImagePath { get; set; }
        public bool AcceptDeletion { get; set; }
        public List<string> Deletions { get; } = [];
        public Task<bool> ConfirmDeleteAsync(string name, string consequences)
        { Deletions.Add(name); return Task.FromResult(AcceptDeletion); }
        public UnsavedChangesDecision Decision { get; set; } = UnsavedChangesDecision.Cancel;
        public Task<string?> PickOpenProjectAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickImageAsync() => Task.FromResult(ImagePath);
        public Task<string?> PickSaveProjectAsync(string name)
        {
            SavePickerCalls++;
            return Task.FromResult<string?>(SavePath);
        }
        public Task<UnsavedChangesDecision> ConfirmUnsavedChangesAsync(string name)
        {
            ConfirmationCalls++;
            return Task.FromResult(Decision);
        }
        public Task ShowErrorAsync(string title, string message) => throw new AssertFailedException($"{title}: {message}");
    }

    private sealed class UiCodeEditor : IExternalCodeEditor
    {
        public bool OpenOnCreation { get; set; } = true;
        public string? Executable { get; private set; }
        public List<string> Opened { get; } = [];
        public void Configure(string? executable) => Executable = executable;
        public void Open(string file) => Opened.Add(file);
    }

    private sealed class UiStore : IProjectStore
    {
        public NovelProject? Saved { get; private set; }
        public int Saves { get; private set; }
        public Task<NovelProject> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Saved!);
        public Task SaveAsync(string path, NovelProject project, CancellationToken cancellationToken = default)
        {
            Saves++;
            Saved = project;
            return Task.CompletedTask;
        }
    }

    private sealed class UiGit : IGitStatusService
    {
        public Task<GitStatusSnapshot> GetStatusAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitStatusSnapshot(GitAvailability.Available, path,
                [new GitFileChange("test-ui.novell", " ", "M")], "Git: 1 изменённый файл"));
    }
}
