using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class GameSessionTests
{
    [TestMethod]
    public async Task ThrowingCleanupRunsRemainingSubscribersAndPublishesUsablePlayer()
    {
        var project = Project(); var session = new GameSession(project, project.Scenes[1].Id, "ru");
        var old = session.Player; var called = 0;
        session.Resetting += () => throw new IOException("first cleanup failure");
        session.Resetting += () => { called++; Assert.IsFalse(session.NewGame(session.Generation));
            if (session.Phase != GameSessionPhase.Ended) Assert.Throws<InvalidOperationException>(() => session.Dispose()); };
        session.Resetting += () => throw new InvalidOperationException("last cleanup failure");
        Assert.IsTrue(session.NewGame(0));
        Assert.AreNotSame(old, session.Player); Assert.AreEqual(1L, session.Generation);
        Assert.AreEqual("yes", session.Player.Execution.State.Variables["fresh"]);
        Assert.AreEqual(2, session.CleanupDiagnostics.Length); Assert.AreEqual(1, called);
        Assert.Throws<ObjectDisposedException>(() => GameSave.Serialize(old.Execution));
        Assert.IsTrue(session.ReturnToMenu(session.Generation)); Assert.AreEqual(2, called);
        var current = session.Player; await session.DisposeAsync(); await session.DisposeAsync();
        Assert.AreEqual(3, called); Assert.AreEqual(GameSessionPhase.Ended, session.Phase);
        Assert.AreEqual(2, session.CleanupDiagnostics.Length);
        Assert.Throws<ObjectDisposedException>(() => GameSave.Serialize(current.Execution));
    }

    private static NovelProject Project(bool lua = false)
    {
        var menu = NovelScene.Create("Меню") with { ScriptBlocks = [new(null, ScriptPhase.SceneStart, new ScriptAction(Guid.NewGuid(), "game.set('menu-ran','bad')", "must-not-run"))] };
        var line = DialogueLine.Create("ru", "Начало игры");
        var story = NovelScene.Create("Игра") with { Lines = [line], ScriptBlocks = lua
            ? [new(null, ScriptPhase.SceneStart, new ScriptAction(Guid.NewGuid(), "game.set('late','old')", "pending"))]
            : [new(null, ScriptPhase.SceneStart, new SetAction(Guid.NewGuid(), "fresh", "yes"))] };
        return NovelProject.Create("Сеансы") with { Scenes = [menu, story], StartScreenSceneId = menu.Id };
    }

    [TestMethod]
    [DataRow(PlaybackMode.Game)] [DataRow(PlaybackMode.AuthorPreview)]
    public async Task MenuNeverRunsScenarioAndRepeatedNewGamesResetAllState(PlaybackMode mode)
    {
        var project = Project();
        await using var session = new GameSession(project, project.Scenes[1].Id, "ru", playbackMode: mode);
        var resets = 0; session.Resetting += () => resets++;
        for (var i = 0; i < 20; i++)
        {
            Assert.AreEqual(GameSessionPhase.Menu, session.Phase);
            Assert.IsNull(session.Player.Frame.LineId); Assert.IsEmpty(session.Player.Execution.State.Variables);
            Assert.IsFalse(session.Player.Execution.HasPendingLua); Assert.IsEmpty(session.Player.DrainAudioEvents());
            session.Player.Tick(TimeSpan.FromSeconds(50)); Assert.AreEqual(0d, session.Player.Execution.State.Time);
            var generation = session.Generation; var id = session.SessionId;
            Assert.IsFalse(session.NewGame(generation, Guid.NewGuid())); Assert.AreEqual(generation, session.Generation);
            Assert.IsTrue(session.NewGame(generation)); Assert.AreNotEqual(id, session.SessionId);
            Assert.IsFalse(session.NewGame(generation)); Assert.IsFalse(session.NewGame(session.Generation));
            Assert.AreEqual("yes", session.Player.Execution.State.Variables["fresh"]);
            Assert.IsFalse(session.Player.Execution.State.Variables.ContainsKey("menu-ran"));
            session.Player.Tick(TimeSpan.FromSeconds(3));
            Assert.IsTrue(session.ReturnToMenu(session.Generation));
            Assert.IsFalse(session.ReturnToMenu(session.Generation));
            Assert.IsFalse(session.NewGame(generation));
        }
        Assert.AreEqual(40L, session.Generation); Assert.AreEqual(40, resets);
    }

    [TestMethod]
    public async Task RetiredPendingLuaCannotCommitIntoMenuOrNextPlaythrough()
    {
        var project = Project(lua: true); await using var session = new GameSession(project, project.Scenes[1].Id, "ru");
        Assert.IsTrue(session.NewGame(0)); var old = session.Player; var state = old.Execution.State;
        Assert.IsTrue(old.Execution.HasPendingLua);
        Assert.IsTrue(session.ReturnToMenu(session.Generation)); Assert.IsFalse(old.Execution.HasPendingLua);
        Assert.IsTrue(session.NewGame(session.Generation)); var next = session.Player;
        await old.DisposeAsync();
        Assert.IsFalse(old.PollLua()); Assert.AreSame(state, old.Execution.State);
        Assert.AreSame(next, session.Player); Assert.IsFalse(next.Execution.State.Variables.ContainsKey("late"));
        Assert.IsTrue(session.ReturnToMenu(session.Generation));
        Assert.IsEmpty(session.Player.Execution.State.Variables);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LateSuccessfulOrFaultedRequestIsObservedAndCannotReplaceNewLifetime(bool failure)
    {
        var project = Project(lua: true);
        var tasks = new List<TaskCompletionSource<ImmutableArray<ScenarioAction>>>();
        var tokens = new List<CancellationToken>();
        Task<ImmutableArray<ScenarioAction>> Run(ScriptAction action, GameState state, NovelProject data, CancellationToken token)
        {
            tokens.Add(token); var completion = new TaskCompletionSource<ImmutableArray<ScenarioAction>>(TaskCreationOptions.RunContinuationsAsynchronously);
            tasks.Add(completion); return completion.Task; // Deliberately ignores cancellation until a late reply arrives.
        }
        await using var session = new GameSession(project, project.Scenes[1].Id, "ru", null, PlaybackMode.Game, Run);
        Assert.IsEmpty(tasks); session.NewGame(0); var old = session.Player;
        var state = old.Execution.State; var generation = session.Generation;
        Assert.Throws<ArgumentException>(() => session.LoadSave("broken"));
        Assert.AreSame(old, session.Player); Assert.AreEqual(generation, session.Generation); Assert.IsFalse(tokens[0].IsCancellationRequested);
        session.ReturnToMenu(session.Generation); Assert.IsTrue(tokens[0].IsCancellationRequested);
        session.NewGame(session.Generation); var current = session.Player;
        if (failure) tasks[0].SetException(new InvalidOperationException("late failure"));
        else tasks[0].SetResult([new SetAction(Guid.NewGuid(), "late", "old")]);
        await old.DisposeAsync(); Assert.IsFalse(old.PollLua()); Assert.AreSame(state, old.Execution.State);
        Assert.AreSame(current, session.Player); Assert.IsTrue(current.Execution.HasPendingLua);
        tasks[1].SetResult([new SetAction(Guid.NewGuid(), "fresh", "new")]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (current.Execution.HasPendingLua) { timeout.Token.ThrowIfCancellationRequested(); current.PollLua(); await Task.Delay(1); }
        Assert.AreEqual("new", current.Execution.State.Variables["fresh"]); Assert.IsFalse(current.Execution.State.Variables.ContainsKey("late"));
    }

    [TestMethod]
    public async Task CleanupRejectsReentrantCommandsAndCancelsBeforePublishingTheNewPlayer()
    {
        var project = Project(); await using var session = new GameSession(project, project.Scenes[1].Id, "ru");
        var called = 0;
        void Reset()
        {
            called++;
            Assert.IsFalse(session.NewGame(session.Generation)); Assert.IsFalse(session.ReturnToMenu(session.Generation));
            Assert.IsFalse(session.NavigateMenu(project.Scenes[0].Id)); Assert.IsFalse(session.End(session.Generation));
            Assert.Throws<InvalidOperationException>(() => session.LoadSave("{}"));
        }
        session.Resetting += Reset;
        session.NewGame(0); session.ReturnToMenu(session.Generation); Assert.AreEqual(2, called);
        Assert.IsEmpty(session.CleanupDiagnostics);
        session.Resetting -= Reset;
    }

    [TestMethod]
    public async Task FailedLoadPreservesPlayerGenerationAndPendingButSuccessfulLoadReplacesWithoutReplay()
    {
        var project = Project(); await using var session = new GameSession(project, project.Scenes[1].Id, "ru");
        session.NewGame(0); var saved = GameSave.Serialize(session.Player.Execution);
        session.ReturnToMenu(session.Generation); var menu = session.Player; var generation = session.Generation;
        Assert.Throws<ArgumentException>(() => session.LoadSave("broken"));
        Assert.AreSame(menu, session.Player); Assert.AreEqual(generation, session.Generation);
        session.LoadSave(saved);
        Assert.AreEqual(GameSessionPhase.Playing, session.Phase);
        Assert.AreEqual(project.Scenes[1].Lines[0].Id, session.Player.Frame.LineId);
        Assert.AreEqual(0d, session.Player.Execution.State.Time);
        Assert.IsFalse(session.Player.Execution.HasPendingLua);
        Assert.AreEqual(1, session.Player.DrainAudioEvents().Length);
    }

    [TestMethod]
    public async Task EndAndDisposeInvalidateCommandsAndClearThePlaythrough()
    {
        var project = Project(); var session = new GameSession(project, project.Scenes[1].Id, "ru");
        session.NewGame(0); var generation = session.Generation;
        Assert.IsFalse(session.End(0)); Assert.IsTrue(session.End(generation));
        Assert.AreEqual(GameSessionPhase.Ended, session.Phase); Assert.IsEmpty(session.Player.Execution.State.Variables);
        Assert.IsFalse(session.End(session.Generation)); Assert.IsFalse(session.NewGame(session.Generation));
        Assert.IsFalse(session.NavigateMenu(project.Scenes[0].Id)); Assert.IsFalse(session.ReturnToMenu(session.Generation));
        Assert.Throws<InvalidOperationException>(() => session.LoadSave("{}"));
        await session.DisposeAsync(); await session.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => session.LoadSave("{}"));
    }

    [TestMethod]
    public async Task MenuNavigationNeverRunsTargetScenarioAndLegacyProjectStillStartsDirectly()
    {
        var project = Project(); await using var session = new GameSession(project, project.Scenes[1].Id, "ru");
        Assert.IsFalse(session.NavigateMenu(Guid.NewGuid())); Assert.IsTrue(session.NavigateMenu(project.Scenes[1].Id));
        Assert.IsNull(session.Player.Frame.LineId); Assert.IsEmpty(session.Player.Execution.State.Variables);
        Assert.IsTrue(session.NewGame(session.Generation, project.Scenes[1].Id));
        Assert.IsFalse(session.NavigateMenu(project.Scenes[0].Id));
        await using var legacy = new GameSession(project with { StartScreenSceneId = null }, project.Scenes[1].Id, "ru");
        Assert.AreEqual(GameSessionPhase.Playing, legacy.Phase); Assert.IsNotNull(legacy.Player.Frame.LineId);
        Assert.IsFalse(legacy.ReturnToMenu(legacy.Generation));
        Assert.Throws<ArgumentException>(() => new GameSession(project, Guid.NewGuid(), "ru"));
        Assert.Throws<ArgumentException>(() => new GameSession(project with { StartScreenSceneId = Guid.NewGuid() }, project.Scenes[1].Id, "ru"));
    }

    [TestMethod]
    public async Task Version17RoundTripStrictMigrationAndStableStartReference()
    {
        var project = Project(); var path = Path.Combine(Path.GetTempPath(), "session-" + Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
            Assert.IsFalse(project.ContentEquals(project with { StartScreenSceneId = null }));
            Assert.AreNotEqual(ScenarioProgram.Fingerprint(project), ScenarioProgram.Fingerprint(project with { StartScreenSceneId = null }));
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["formatVersion"] = 16;
            File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            json.AsObject().Remove("startScreenSceneId"); var original = json.ToJsonString(); File.WriteAllText(path, original);
            var migrated = await store.LoadAsync(path); Assert.IsNull(migrated.StartScreenSceneId);
            Assert.AreEqual(17, migrated.FormatVersion); Assert.AreEqual(original, File.ReadAllText(path));
            json["formatVersion"] = 17; File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            json["startScreenSceneId"] = Guid.NewGuid(); File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(ScreenButtonAction.NewGame)] [DataRow(ScreenButtonAction.MainMenu)] [DataRow(ScreenButtonAction.EndSession)]
    public async Task SessionCommandsRoundTripAndOldFormatsRejectThem(ScreenButtonAction action)
    {
        var project = Project(); var menu = project.Scenes[0]; var button = new ScreenNode(Guid.NewGuid(), menu.Canvas.Nodes[0].Id, ScreenNodeKind.Button, new(0, 0, 100, 40));
        var target = action == ScreenButtonAction.NewGame ? project.Scenes[1].Id : (Guid?)null;
        menu = menu with { Canvas = menu.Canvas with { Nodes = menu.Canvas.Nodes.Add(button), Buttons = [new(button.Id, action, target)] } };
        project = project with { Scenes = project.Scenes.SetItem(0, menu), InputBindings = [new("F2", action, target)] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project); Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
            var json = JsonNode.Parse(File.ReadAllText(path))!; json["formatVersion"] = 16; json.AsObject().Remove("startScreenSceneId");
            File.WriteAllText(path, json.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }
}
