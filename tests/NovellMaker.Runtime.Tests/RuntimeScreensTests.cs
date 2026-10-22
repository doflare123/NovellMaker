using System.Collections.Immutable;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Projects;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeScreensTests
{
    [TestMethod]
    public void DirectServiceBindingUsesOriginGenerationAndClosedOriginCannotReceiveResults()
    {
        var scene = ScreenFixture.Create("UI").ToScene(); var script = new NovellMaker.Core.Execution.LuaScriptAsset(Guid.NewGuid(), "ui.lua", "return {on_event=function(e) end}");
        scene = scene with { Canvas = scene.Canvas with { HandlerScriptId = script.Id, Buttons = [new(scene.Canvas.Nodes[2].Id, ScreenButtonAction.ListSlots, null)] } };
        var project = NovelProject.Create("Сервисы") with { Scenes = [scene], Scripts = [script] };
        long generation = 3; long received = -1; ScreenInstance? origin = null;
        using var host = new RuntimeScreens(Guid.NewGuid(), enableLua: true, hostService: (instance, command, captured) => { origin = instance; received = captured; });
        host.Configure(project, () => ImmutableDictionary<string, string>.Empty, () => generation); host.SyncScene(scene.Id);
        var current = host.Stack.Instances.Single(); generation = 4;
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); Assert.AreSame(current, origin); Assert.AreEqual(3L, received);
        host.Stack.Close(current); var queued = host.Scripts!.QueuedEvents;
        Assert.IsFalse(host.DeliverServiceResult(origin, new(Guid.NewGuid(), NovellMaker.Core.Execution.GameServiceAction.ListSlots, true, "Список", null, [])));
        Assert.AreEqual(queued, host.Scripts.QueuedEvents);
    }

    [TestMethod]
    public void ReplacingAndTogglingAuthorLayersPreservesUnderlyingSceneAndRoutesBackAndClose()
    {
        var a = ScreenFixture.Create("A"); var b = ScreenFixture.Create("B"); var c = ScreenFixture.Create("C");
        a = a with { Buttons = [new(a.Nodes[2].Id, ScreenButtonAction.Open, b.Id), new(a.Nodes[3].Id, ScreenButtonAction.Toggle, b.Id)] };
        b = b with { Buttons = [new(b.Nodes[2].Id, ScreenButtonAction.Replace, c.Id), new(b.Nodes[3].Id, ScreenButtonAction.Back, null)] };
        var project = NovelProject.Create("Слои") with { Scenes = [a.ToScene(), b.ToScene(), c.ToScene()], InputBindings = [new("F9", ScreenButtonAction.Toggle, b.Id), new("Escape", ScreenButtonAction.Back, null)] };
        using var host = new RuntimeScreens(Guid.NewGuid()); host.Configure(project, () => ImmutableDictionary<string, string>.Empty); host.SyncScene(a.Id);
        var original = host.Stack.Instances.Single();
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); var replaced = host.Stack.Instances.Last();
        host.Route(new(ScreenInputKind.Activate)); Assert.HasCount(2, host.Stack.Instances); Assert.AreEqual(c.Id, host.Stack.Instances.Last().Definition.Id);
        Assert.AreNotEqual(replaced.Id, host.Stack.Instances.Last().Id); host.Route(new(ScreenInputKind.Activate)); Assert.AreSame(original, host.Stack.Instances.Single());
        host.Route(new(ScreenInputKind.PointerDown, 450, 390)); host.Route(new(ScreenInputKind.PointerUp, 450, 390));
        Assert.HasCount(2, host.Stack.Instances); host.RouteKey("F9"); Assert.AreSame(original, host.Stack.Instances.Single());
        host.RouteKey("F9"); host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate));
        Assert.AreSame(original, host.Stack.Instances.Single()); host.RouteKey("Escape"); Assert.AreSame(original, host.Stack.Instances.Single());
        host.Configure(project.WithCanvas(a with { Buttons = [new(a.Nodes[2].Id, ScreenButtonAction.Replace, c.Id)] }), () => ImmutableDictionary<string, string>.Empty);
        host.SyncScene(a.Id); host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); Assert.AreEqual(c.Id, host.NavigationRequested);
    }

    [TestMethod]
    public void UnassignedAndUnavailableHandlersRemainRecoverableAndForeignInstancesDoNotRunProjectCommands()
    {
        using var host = new RuntimeScreens(Guid.NewGuid()); host.SyncScene(Guid.NewGuid()); Assert.IsEmpty(host.Stack.Instances);
        var asset = ScreenFixture.Create("Автор"); asset = asset with { Buttons = [new(asset.Nodes[2].Id, ScreenButtonAction.None, null)] };
        var project = NovelProject.Create("Проект") with { Scenes = [asset.ToScene()], InputBindings = [new("A", ScreenButtonAction.Open, asset.Id)] };
        host.Configure(project, () => ImmutableDictionary<string, string>.Empty); host.RouteKey("A"); host.Route(new(ScreenInputKind.Activate)); Assert.IsEmpty(host.Diagnostics.ToArray());
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); StringAssert.Contains(host.Diagnostics.Last(), "недоступен");
        host.Route(new(ScreenInputKind.Back)); Assert.IsEmpty(host.Stack.Instances);
        var foreign = host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Чужой").Definition()); host.Route(new(ScreenInputKind.Activate)); Assert.IsNull(host.NavigationRequested);
        Assert.AreSame(foreign, host.Stack.Instances.Single());
        var script = new NovellMaker.Core.Execution.LuaScriptAsset(Guid.NewGuid(), "script.lua", "return {on_event=function() end}");
        host.Configure(project.WithCanvas(asset with { HandlerScriptId = script.Id }) with { Scripts = [script] }, () => ImmutableDictionary<string, string>.Empty.Add(new string('a', 129), "value"));
        // A host without Lua cannot create a VM; an enabled host contains invalid captured data as a diagnostic.
        using var scripted = new RuntimeScreens(Guid.NewGuid(), enableLua: true);
        scripted.Configure(project.WithCanvas(asset with { HandlerScriptId = script.Id }) with { Scripts = [script] }, () => ImmutableDictionary<string, string>.Empty.Add(new string('a', 129), "value"));
        scripted.RouteKey("A"); Assert.HasCount(1, scripted.Stack.Instances); Assert.IsTrue(scripted.Diagnostics.Any());
    }
    [TestMethod]
    public void EmptyProjectHasNoGameShortcutsOrEngineScenes()
    {
        using var host = new RuntimeScreens(Guid.NewGuid()); host.Configure(NovelProject.Create("Пустой"), () => ImmutableDictionary<string, string>.Empty);
        foreach (var key in new[] { "P", "F9", "F7", "F8" }) Assert.IsFalse(host.RouteKey(key));
        Assert.IsEmpty(host.Stack.Instances); Assert.IsEmpty(host.Diagnostics.ToArray());
        var empty = ScreenAsset.Create("Пустая сцена"); Assert.HasCount(1, empty.Nodes); Assert.IsEmpty(empty.Buttons); Assert.IsEmpty(empty.References);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void F9AtInstanceOrNodeLimitIsAtomicAndDoesNotThrow(bool nodeLimit)
    {
        var full = ScreenFixture.Create("Стек"); var target = ScreenFixture.Create("Мой модуль");
        if (nodeLimit) full = full with { Nodes = [full.Nodes[0], .. Enumerable.Range(0, 1023).Select(_ => new ScreenNode(Guid.NewGuid(), full.Nodes[0].Id, ScreenNodeKind.Text, new(0, 0, 1, 1)))] , References = ImmutableDictionary<string, Guid>.Empty, Buttons = [] };
        var project = NovelProject.Create("Тест") with { Scenes = [full.ToScene(), target.ToScene()], InputBindings = [new("F9", ScreenButtonAction.Open, target.Id)] };
        using var host = new RuntimeScreens(Guid.NewGuid()); host.Configure(project, () => ImmutableDictionary<string, string>.Empty);
        for (var i = 0; i < (nodeLimit ? 4 : 32); i++) host.Stack.Open(Guid.NewGuid(), full.Definition());
        var instances = host.Stack.Instances;
        Assert.IsTrue(host.RouteKey("F9")); CollectionAssert.AreEqual(instances.ToArray(), host.Stack.Instances.ToArray());
        Assert.HasCount(1, host.Diagnostics.ToArray()); Assert.IsTrue(host.Route(new(ScreenInputKind.Back)).Consumed);
        Assert.IsTrue(host.RouteKey("F9")); Assert.AreEqual(target.Id, host.Stack.Instances.Last().Definition.Id);
    }

    [TestMethod]
    public void ReleaseFromPreviousSceneCannotExecuteItsNavigationAfterTimerJump()
    {
        var a = ScreenFixture.Create("A"); var b = ScreenFixture.Create("B"); var c = ScreenFixture.Create("C");
        a = a with { Buttons = [new(a.Nodes[2].Id, ScreenButtonAction.Scene, c.Id)] };
        b = b with { Buttons = [new(b.Nodes[2].Id, ScreenButtonAction.Scene, c.Id)] };
        var entry = Guid.NewGuid();
        var project = NovelProject.Create("Гонка") with { Scenes = [a.ToScene() with { UseActions = true, Actions = [new NovellMaker.Core.Execution.WaitAction(Guid.NewGuid(), "timer", 1), new NovellMaker.Core.Execution.JumpAction(Guid.NewGuid(), b.Id, entry)] }, b.ToScene() with { UseActions = true, Actions = [new NovellMaker.Core.Execution.WaitAction(entry, "input")] }, c.ToScene()] };
        using var player = new NovellMaker.Core.Execution.ScenarioPlayer(project, a.Id, "ru");
        using var host = new RuntimeScreens(Guid.NewGuid()); host.Configure(project, () => player.State.Variables);
        host.Route(new(ScreenInputKind.PointerDown, 450, 320), player.State.SceneId);
        player.Tick(TimeSpan.FromSeconds(1)); Assert.AreEqual(b.Id, player.State.SceneId);
        host.Route(new(ScreenInputKind.PointerUp, 450, 320), player.State.SceneId);
        Assert.IsNull(host.NavigationRequested); Assert.AreEqual(b.Id, host.Stack.Instances.Single().Definition.Id);
        host.Route(new(ScreenInputKind.PointerDown, 450, 320), player.State.SceneId);
        host.Route(new(ScreenInputKind.PointerUp, 450, 320), player.State.SceneId); Assert.AreEqual(c.Id, host.NavigationRequested);
    }

    [TestMethod]
    public void ButtonsDispatchGenericHostCommandsAndProjectKeysCanBeRebound()
    {
        var scene = ScreenFixture.Create("Авторская логика");
        scene = scene with { Buttons = [new(scene.Nodes[2].Id, ScreenButtonAction.Volume, null) { Group = NovellMaker.Core.Audio.AudioGroup.Sound, Amount = -.1 }, new(scene.Nodes[3].Id, ScreenButtonAction.Mute, null) { Group = NovellMaker.Core.Audio.AudioGroup.Voice }] };
        var received = new List<ScreenButtonBinding>();
        var project = NovelProject.Create("Тест") with { Scenes = [scene.ToScene()], InputBindings = [new("F4", ScreenButtonAction.Open, scene.Id), new("F2", ScreenButtonAction.Save, null)] };
        using var host = new RuntimeScreens(Guid.NewGuid(), hostAction: received.Add); host.Configure(project, () => ImmutableDictionary<string, string>.Empty);
        Assert.IsFalse(host.RouteKey("F9")); Assert.IsTrue(host.RouteKey("F4"));
        host.Route(new(ScreenInputKind.Activate)); host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); host.RouteKey("F2");
        Assert.AreEqual(ScreenButtonAction.Volume, received[0].Action); Assert.AreEqual(-.1, received[0].Amount);
        Assert.AreEqual(NovellMaker.Core.Audio.AudioGroup.Voice, received[1].Group); Assert.AreEqual(ScreenButtonAction.Save, received[2].Action);
    }
    [TestMethod]
    public void SceneComponentsShareStackButDoNotPauseOrConsumeStoryContinuationWithoutFocus()
    {
        var scene = ScreenFixture.Create("Сюжет с кнопкой"); var overlay = ScreenFixture.Create("Слой");
        scene = scene with { Buttons = [new(scene.Nodes[2].Id, ScreenButtonAction.Open, overlay.Id)] };
        overlay = overlay with { Buttons = [new(overlay.Nodes[2].Id, ScreenButtonAction.Close, null)] };
        var project = NovellMaker.Core.Projects.NovelProject.Create("Новелла") with { Scenes = [scene.ToScene(), overlay.ToScene()], InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, overlay.Id)] };
        using var host = new RuntimeScreens(Guid.NewGuid()); host.Configure(project, () => ImmutableDictionary<string, string>.Empty);
        host.SyncScene(scene.Id); var original = host.Stack.Instances.Single();
        Assert.IsFalse(host.HasOverlays); Assert.IsFalse(host.Stack.PausesScenario);
        Assert.IsFalse(host.Route(new(ScreenInputKind.Activate)).Consumed); Assert.IsFalse(host.Route(new(ScreenInputKind.Back)).Consumed);
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate));
        Assert.IsTrue(host.HasOverlays); Assert.IsTrue(host.Stack.PausesScenario);
        host.Route(new(ScreenInputKind.Activate)); Assert.AreSame(original, host.Stack.Instances.Single());
        Assert.IsFalse(host.Stack.PausesScenario); host.RouteKey("P"); host.RouteKey("P"); Assert.AreSame(original, host.Stack.Instances.Single());
        host.SyncScene(scene.Id); Assert.AreSame(original, host.Stack.Instances.Single());
        host.SyncScene(overlay.Id); Assert.AreNotSame(original, host.Stack.Instances.Single());
    }

    [TestMethod]
    public void SceneOverlayPoliciesAndNavigationAreIndependentOfResourceKind()
    {
        var baseScene = ScreenFixture.Create("Сцена"); var layer = ScreenFixture.Create("Слой") with { PauseOnOverlay = false, BlockInputOnOverlay = false };
        baseScene = baseScene with { Buttons = [new(baseScene.Nodes[2].Id, ScreenButtonAction.Open, layer.Id)] };
        layer = layer with { Buttons = [new(layer.Nodes[2].Id, ScreenButtonAction.Scene, baseScene.Id)] };
        var project = NovellMaker.Core.Projects.NovelProject.Create("Новелла") with { Scenes = [baseScene.ToScene(), layer.ToScene()] };
        using var host = new RuntimeScreens(Guid.NewGuid()); host.Configure(project, () => ImmutableDictionary<string, string>.Empty); host.SyncScene(baseScene.Id);
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate));
        Assert.IsTrue(host.HasOverlays); Assert.IsFalse(host.Stack.PausesScenario); Assert.IsFalse(host.Stack.Instances.Last().Policy.Modal);
        host.Route(new(ScreenInputKind.Activate)); Assert.AreEqual(baseScene.Id, host.NavigationRequested);
        host.Reset(); Assert.IsNull(host.NavigationRequested); Assert.IsEmpty(host.Stack.Instances);
    }

    [TestMethod]
    public async Task SuccessfulLoadRebuildsSceneComponentsWithoutRunningLuaUntilExplicitInteraction()
    {
        var asset = ScreenFixture.Create("Сцена");
        var script = new NovellMaker.Core.Execution.LuaScriptAsset(Guid.NewGuid(), "scene.lua", "return {on_load=function() ui.set_text(refs.title,'loaded') end,on_event=function() ui.set_text(refs.title,'clicked') end}");
        asset = asset with { HandlerScriptId = script.Id, Buttons = [new(asset.Nodes[2].Id, ScreenButtonAction.Lua, null)] };
        var project = NovellMaker.Core.Projects.NovelProject.Create("Новелла") with { Scenes = [asset.ToScene()], Scripts = [script] };
        await using var host = new RuntimeScreens(Guid.NewGuid(), enableLua: true); host.Configure(project, () => ImmutableDictionary<string, string>.Empty);
        host.SyncScene(asset.Id); await Drain(host); Assert.AreEqual("loaded", host.Stack.Instances.Single().Definition.Nodes[1].Text);
        host.Reset(); host.SyncScene(asset.Id, afterLoad: true); var batches = host.Scripts!.AppliedBatches;
        Assert.IsFalse(host.HasPending); host.Poll(); Assert.AreEqual(batches, host.Scripts.AppliedBatches);
        Assert.AreEqual("Пауза", host.Stack.Instances.Single().Definition.Nodes[1].Text);
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); await Drain(host);
        Assert.AreEqual("clicked", host.Stack.Instances.Single().Definition.Nodes[1].Text);
    }

    [TestMethod]
    public async Task AuthorHandlerUsesOwnRefsCurrentEventSnapshotAndEscapeAfterFailure()
    {
        var asset = ScreenFixture.Create("Авторская пауза");
        var script = new NovellMaker.Core.Execution.LuaScriptAsset(Guid.NewGuid(), "pause.lua", """
            local count = 0
            return {
              on_load = function() ui.set_text(refs.title, ui.get('value')) end,
              on_event = function(event)
                count = count + 1
                if count == 2 then local fail; fail() end
                ui.set_text(refs.title, ui.get('value'))
              end
            }
            """);
        asset = asset with { HandlerScriptId = script.Id, Buttons = [new(asset.Nodes[2].Id, ScreenButtonAction.Lua, null)] };
        var project = NovellMaker.Core.Projects.NovelProject.Create("Меню") with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[asset]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray(), Scripts = [script], InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, asset.Id)] };
        var value = "load";
        await using var menus = new RuntimeScreens(Guid.NewGuid(), enableLua: true);
        menus.Configure(project, () => System.Collections.Immutable.ImmutableDictionary<string, string>.Empty.Add("value", value));
        menus.RouteKey("P"); await Drain(menus);
        Assert.AreEqual("load", menus.Stack.Instances.Single().Definition.Nodes[1].Text);
        value = "click"; menus.Route(new(ScreenInputKind.Activate)); await Drain(menus);
        Assert.AreEqual("click", menus.Stack.Instances.Single().Definition.Nodes[1].Text);
        menus.Route(new(ScreenInputKind.Activate)); await Drain(menus); Assert.IsTrue(menus.Diagnostics.Any());
        Assert.AreEqual("click", menus.Stack.Instances.Single().Definition.Nodes[1].Text, "A failed handler cannot partially change a menu.");
        menus.Route(new(ScreenInputKind.Activate)); Assert.IsTrue(menus.Diagnostics.Any());
        Assert.IsTrue(menus.Route(new(ScreenInputKind.Back)).Consumed); await Drain(menus);
        Assert.IsEmpty(menus.Stack.Instances);
        value = "new instance"; menus.RouteKey("P"); await Drain(menus);
        Assert.AreEqual("new instance", menus.Stack.Instances.Single().Definition.Nodes[1].Text);
    }

    [TestMethod]
    public void AuthorCloseConsumesClickAndStackLimitDoesNotDestroyExistingMenus()
    {
        var asset = ScreenFixture.Create("Закрыть");
        var project = NovellMaker.Core.Projects.NovelProject.Create("Меню") with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[asset]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray(), InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, asset.Id)] };
        using var menus = new RuntimeScreens(Guid.NewGuid());
        menus.Configure(project, () => System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);
        menus.RouteKey("P"); menus.Route(new(ScreenInputKind.PointerDown, 450, 320));
        Assert.IsTrue(menus.Route(new(ScreenInputKind.PointerUp, 450, 320)).Consumed); Assert.IsEmpty(menus.Stack.Instances);
        asset = asset with { Buttons = [new(asset.Nodes[2].Id, ScreenButtonAction.Open, asset.Id)] };
        menus.Configure(project with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[asset]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()}, () => System.Collections.Immutable.ImmutableDictionary<string, string>.Empty);
        menus.RouteKey("P");
        for (var i = 0; i < 35; i++) menus.Route(new(ScreenInputKind.Activate));
        Assert.HasCount(ScreenStack.MaxInstances, menus.Stack.Instances); Assert.IsTrue(menus.Diagnostics.Any());
        menus.RouteKey("P"); Assert.HasCount(ScreenStack.MaxInstances - 1, menus.Stack.Instances);
    }

    private static async Task Drain(RuntimeScreens menus)
    {
        for (var i = 0; i < 1000 && menus.HasPending; i++)
        {
            menus.Poll();
            if (menus.Scripts?.PendingTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(10));
            else await Task.Delay(1);
            menus.Poll();
        }
        Assert.IsFalse(menus.HasPending);
    }

}
