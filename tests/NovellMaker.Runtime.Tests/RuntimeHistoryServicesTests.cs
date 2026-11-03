using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeHistoryServicesTests
{
    [TestMethod]
    public void ReplayRequiresRecordedVisitAndStopsWithOriginOrGenerationAndPausesWhenCovered()
    {
        var story = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("en", "Hello")] };
        var project = ReadingScreenTemplates.Ensure(NovelProject.Create("History") with { Scenes = [story],
            InputBindings = [new("H", ScreenButtonAction.History, null)] });
        using var session = new GameSession(project, story.Id, "en");
        var stack = new ScreenStack(Guid.NewGuid());
        var screen = project.Scenes.Single(s => s.Id == project.InputBindings[0].TargetSceneId);
        var origin = stack.Open(Guid.NewGuid(), screen.AsScreen().Definition());
        var plays = 0; var stops = 0; var paused = false; GameServiceResult? result = null;
        var bridge = new RuntimeHistoryServices(session, (_, _) => true, (_, language) => { Assert.AreEqual("en", language); plays++; return true; },
            () => stops++, value => paused = value) { IsCurrentOrigin = i => stack.Instances.Contains(i) };
        bridge.Completed += (_, value) => result = value;
        var state = session.Player.Execution.State;
        var visit = state.History.Single();
        Assert.IsTrue(bridge.Request(origin, new(Guid.NewGuid(), GameServiceAction.GetHistory) { HistoryIndex = 0, HistoryPage = 1, HistoryPageSize = 700 }, session.Generation));
        Assert.AreEqual("Hello", result!.History!.Text); Assert.AreSame(state, session.Player.Execution.State);
        GameServiceCommand Replay(Guid id) => new(Guid.NewGuid(), GameServiceAction.ReplayHistoryVoice) { VisitId = id };
        Assert.IsFalse(bridge.Request(origin, Replay(Guid.NewGuid()), session.Generation)); Assert.AreEqual("history_unavailable", result.Message); Assert.AreEqual(0, plays);
        Assert.IsTrue(bridge.Request(origin, Replay(visit.VisitId), session.Generation)); Assert.AreEqual(1, plays);
        bridge.Update(true, origin); Assert.IsFalse(paused);
        var cover = stack.Open(Guid.NewGuid(), screen.AsScreen().Definition()); bridge.Update(true, cover); Assert.IsTrue(paused);
        stack.Close(cover); bridge.Update(true, origin); Assert.IsFalse(paused);
        bridge.Update(false, origin); Assert.IsTrue(paused);
        stack.Close(origin); bridge.Update(true, null); Assert.AreEqual(1, stops);
        Assert.IsFalse(bridge.Request(origin, Replay(visit.VisitId), session.Generation)); Assert.AreEqual(1, plays);
        origin = stack.Open(Guid.NewGuid(), screen.AsScreen().Definition());
        Assert.IsTrue(bridge.Request(origin, Replay(visit.VisitId), session.Generation));
        var generation = session.Generation; Assert.IsTrue(session.End(generation));
        bridge.Update(true, origin); Assert.AreEqual(2, stops);
        Assert.IsFalse(bridge.Request(origin, Replay(visit.VisitId), generation)); Assert.AreEqual(2, plays);
    }

    [TestMethod]
    [DataRow(ScreenButtonAction.History, "H")]
    [DataRow(ScreenButtonAction.ReadingSettings, "R")]
    public void AuthoredReadingActionsOpenTheirSelectedSceneAndToggleBackWithoutRebuildingIt(ScreenButtonAction action, string key)
    {
        var story = NovelScene.Create("Story");
        var project = ReadingScreenTemplates.Ensure(NovelProject.Create("Templates") with { Scenes = [story], InputBindings = [new(key, action, null)] });
        var target = project.Scenes.Single(s => s.Id == project.InputBindings[0].TargetSceneId);
        var title = target.Canvas.Nodes.Single(n => n.Name == "title");
        project = project.WithCanvas(target.AsScreen() with { Nodes = target.Canvas.Nodes.Replace(title, title with { Text = "Author's screen", Bounds = new(30, 20, 400, 48) }) });
        using var screens = new RuntimeScreens(Guid.NewGuid()); screens.Configure(project, () => ImmutableDictionary<string, string>.Empty);
        screens.SyncScene(story.Id); Assert.IsTrue(screens.RouteKey(key));
        var opened = screens.Stack.Instances.Single(); Assert.AreEqual(target.Id, opened.Definition.Id);
        Assert.IsTrue(opened.Policy.Modal); Assert.IsTrue(opened.Policy.PauseScenario);
        Assert.AreEqual("Author's screen", opened.Definition.Nodes.Single(n => n.Id == title.Id).Text);
        screens.RouteKey(key); Assert.IsFalse(screens.HasOverlays);
        screens.RouteKey(key); Assert.AreNotEqual(opened.Id, screens.Stack.Instances.Single().Id);
        screens.Route(new(ScreenInputKind.Back)); Assert.IsFalse(screens.HasOverlays);
    }
}
