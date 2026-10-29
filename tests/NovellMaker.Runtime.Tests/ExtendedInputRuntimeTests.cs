using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class ExtendedInputRuntimeTests
{
    [TestMethod]
    public async Task FirstKeyAfterDeferredLoadLoadsHandlerAndConsumesRootInput()
    {
        var scene = ScreenFixture.Create("Ввод после Load").ToScene();
        var script = new LuaScriptAsset(Guid.NewGuid(), "input.lua", "return {input_events=true,on_event=function(e) if e.kind=='key_down' then ui.set_text(refs.title,e.key) end end}");
        scene = scene with { Canvas = scene.Canvas with { HandlerScriptId = script.Id } };
        var project = NovelProject.Create("Ввод") with { Scenes = [scene], Scripts = [script] };
        await using var host = new RuntimeScreens(Guid.NewGuid(), true);
        host.Configure(project, () => ImmutableDictionary<string, string>.Empty); host.SyncScene(scene.Id, afterLoad: true);
        Assert.IsFalse(host.HasPending);
        var modal = host.Stack.Open(Guid.NewGuid(), ScreenAsset.Create("Меню").Definition());
        host.RouteKeyboard(new(ScreenInputKind.KeyDown, Key: "B")); Assert.IsFalse(host.HasPending);
        host.Stack.Close(modal);
        Assert.IsTrue(host.RouteKeyboard(new(ScreenInputKind.KeyDown, Key: "A")));
        await Drain(host); Assert.AreEqual("A", host.Stack.Instances.Single().Definition.Nodes[1].Text); Assert.IsEmpty(host.Diagnostics);
    }
    [TestMethod]
    public async Task MinigameCaptureBlocksSaveModalConsumesKeysAndLoadReleasesOldInput()
    {
        var game = ScreenFixture.Create("Ввод").ToScene(); var modal = ScreenAsset.Create("Меню").ToScene();
        var script = new LuaScriptAsset(Guid.NewGuid(), "input.lua", "return {input_events=true,on_event=function(e) if e.kind=='key_down' then ui.set_text(refs.label,e.key) end end}");
        game = game with { Canvas = game.Canvas with { HandlerScriptId = script.Id, Buttons = [],
            References = ImmutableDictionary<string, Guid>.Empty.Add("label", game.Canvas.Nodes[1].Id), Minigame = new() } };
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "После мини-игры")],
            ScriptBlocks = [new(null, ScriptPhase.SceneStart, new StartMinigameAction(Guid.NewGuid(), game.Id, LuaValue.FromObject([])))] };
        var project = NovelProject.Create("Ввод") with { Scenes = [story, game, modal], Scripts = [script], InputBindings = [new("F10", ScreenButtonAction.Toggle, modal.Id)] };
        using var player = new ScenarioPlayer(project, story.Id, "ru"); await using var host = new RuntimeScreens(Guid.NewGuid(), true);
        host.Configure(project, () => player.State.Variables, execution: () => player); host.SyncScene(story.Id); await Drain(host);
        var saved = GameSave.Serialize(player); var instance = host.Stack.Instances.Single();
        host.Route(new(ScreenInputKind.PointerDown, 450, 320)); await Drain(host);
        Assert.IsNotNull(host.Stack.PointerCapture); Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player));
        Assert.Throws<ArgumentException>(() => GameSave.Load(player, "{}")); host.Poll(); Assert.IsNotNull(host.Stack.PointerCapture);
        host.RouteKey("F10"); host.Poll(); Assert.IsNull(host.Stack.PointerCapture);
        Assert.IsTrue(host.RouteKeyboard(new(ScreenInputKind.KeyDown, Key: "Left")));
        host.RouteKeyboard(new(ScreenInputKind.KeyUp, Key: "Left")); Assert.AreEqual("Пауза", instance.Definition.Nodes[1].Text);
        host.RouteKey("F10"); await Drain(host); Assert.IsNotNull(GameSave.Capture(player));
        host.RouteKeyboard(new(ScreenInputKind.KeyDown, Key: "A")); await Drain(host);
        Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player));
        host.RouteKeyboard(new(ScreenInputKind.KeyUp, Key: "A")); await Drain(host); Assert.IsNotNull(GameSave.Capture(player));
        host.Route(new(ScreenInputKind.PointerDown, 450, 320)); await Drain(host); Assert.IsNotNull(host.Stack.PointerCapture);
        GameSave.Load(player, saved);
        // RuntimeGame receives GameSession.Resetting on a successful load, including the same RunId.
        host.Reset(); host.SyncScene(player.State.SceneId, afterLoad: true); host.Poll(); await Drain(host);
        Assert.IsNull(host.Stack.PointerCapture); Assert.AreNotSame(instance, host.Stack.Instances.Single()); Assert.IsEmpty(host.Diagnostics);
    }

    [TestMethod]
    public void CapturedCoordinatesPreserveOutsideLetterboxDistance()
    {
        var box = RuntimeInputMapper.CalculateLetterbox(900, 900);
        var outside = RuntimeInputMapper.MapCapturedPointer(new(-50, -50), box);
        Assert.IsLessThan(0, outside.X); Assert.IsLessThan(0, outside.Y);
        var clamped = RuntimeInputMapper.MapCapturedPointer(new(int.MaxValue, int.MinValue), box);
        Assert.AreEqual(100000, clamped.X); Assert.AreEqual(-100000, clamped.Y);
        Assert.AreEqual(-1, RuntimeInputMapper.MapCapturedPointer(new(-1, 0), new(0, 0, 2560, 1440)).X);
    }
    private static async Task Drain(RuntimeScreens host)
    {
        for (var n = 0; n < 3000; n++)
        { host.Poll(); if (!host.HasPending) return; await Task.Delay(2); }
        Assert.Fail("Input runtime did not settle.");
    }
}
