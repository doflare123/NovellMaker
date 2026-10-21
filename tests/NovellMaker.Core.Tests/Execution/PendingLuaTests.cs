using System.Collections.Immutable;
using System.Text.Json;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class PendingLuaTests
{
    [TestMethod]
    [DataRow(0)] [DataRow(80)]
    public void EnterSceneResetsActionAndLuaCallBudgetsOfCancelledPass(int actionsBeforeLua)
    {
        var scene = NovelScene.Create("Pending") with { UseActions = true, Actions = [
            .. Enumerable.Range(0, actionsBeforeLua).Select(i => new SetAction(Guid.NewGuid(), "value", i.ToString())),
            new ScriptAction(Guid.NewGuid(), "", "pending.lua")] };
        var project = NovelProject.Create("Переходы") with { Scenes = [scene] };
        var calls = 0;
        Task<ImmutableArray<ScenarioAction>> Run(ScriptAction script, GameState state, NovelProject data, CancellationToken token)
        {
            calls++; var task = new TaskCompletionSource<ImmutableArray<ScenarioAction>>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => task.TrySetCanceled(token)); return task.Task;
        }
        using var player = new ScenarioPlayer(project, scene.Id, "ru", Run);
        for (var i = 0; i < 45; i++)
        {
            Assert.IsTrue(player.EnterScene(scene.Id)); Assert.IsTrue(player.HasPendingLua); Assert.IsNull(player.State.Error);
        }
        Assert.AreEqual(46, calls);
    }

    [TestMethod]
    public async Task EnterSceneStartsFreshWallTimeBudgetAfterCancelledPassDeadline()
    {
        var scene = NovelScene.Create("Pending") with { UseActions = true, Actions = [new ScriptAction(Guid.NewGuid(), "", "pending.lua")] };
        var project = NovelProject.Create("Переходы") with { Scenes = [scene] };
        Task<ImmutableArray<ScenarioAction>> Run(ScriptAction script, GameState state, NovelProject data, CancellationToken token)
        {
            var task = new TaskCompletionSource<ImmutableArray<ScenarioAction>>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.Register(() => task.TrySetCanceled(token)); return task.Task;
        }
        using var player = new ScenarioPlayer(project, scene.Id, "ru", Run);
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        Assert.IsTrue(player.EnterScene(scene.Id)); Assert.IsTrue(player.HasPendingLua); Assert.IsNull(player.State.Error);
    }
    [TestMethod]
    public void ConstructorAndFramesReturnWithStablePositionUntilHostAppliesResultOnce()
    {
        var (project, scene, script, _) = Fixture();
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        var before = player.State;
        Assert.IsTrue(player.HasPendingLua);
        Assert.AreEqual(script.Id, before.NextActionId);
        for (var i = 0; i < 10; i++) player.Tick(TimeSpan.FromSeconds(1));
        Assert.AreSame(before, player.State);
        Assert.IsFalse(player.Advance()); Assert.IsFalse(player.Click(Guid.NewGuid())); Assert.IsFalse(player.Choose(Guid.NewGuid()));
        runner.Complete(new SetAction(Guid.NewGuid(), "answer", "yes"));
        Assert.AreSame(before, player.State, "A Task completion must never mutate game state.");
        Assert.IsTrue(PollReady(player));
        Assert.AreEqual("yes", player.State.Variables["answer"]);
        var applied = player.State;
        Assert.IsFalse(PollReady(player));
        Assert.AreSame(applied, player.State);
        Assert.AreEqual(0d, player.State.Time);
        Assert.AreEqual("input", player.State.Wait?.Mode);
    }

    [TestMethod]
    public void PauseHoldsCompletedResultAndCompletionFrameDoesNotAdvanceMotionOrTimer()
    {
        var (project, scene, _, actor) = Fixture(motion: true);
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        player.IsPaused = true;
        runner.Complete(new WaitAction(Guid.NewGuid(), "timer", 2));
        var before = player.State;
        Assert.IsFalse(PollReady(player));
        player.Tick(TimeSpan.FromSeconds(100)); Assert.AreSame(before, player.State);
        player.IsPaused = false;
        player.Tick(TimeSpan.FromSeconds(100));
        Assert.AreEqual(0d, player.State.Time);
        Assert.AreEqual(0d, player.State.ActiveActions.Single().Elapsed);
        Assert.AreEqual(2d, player.State.Wait!.Remaining);
        player.Tick(TimeSpan.FromSeconds(1));
        Assert.AreEqual(1d, player.State.Time);
        Assert.AreEqual(0.35, player.State.Placements[actor.Id].X, 1e-9);
        Assert.AreEqual(1d, player.State.Wait!.Remaining);
    }

    [TestMethod]
    public async Task PendingSaveDoesNotReplaceAnExistingSlotOrWriteTemporaryFiles()
    {
        var (project, scene, _, _) = Fixture();
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        Assert.Throws<InvalidOperationException>(() => GameSave.Serialize(player));
        var directory = Path.Combine(Path.GetTempPath(), "novell-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "slot.json");
        try
        {
            await File.WriteAllTextAsync(path, "previous-slot");
            await Assert.ThrowsAsync<InvalidOperationException>(() => GameSave.SaveAsync(path, player));
            Assert.AreEqual("previous-slot", await File.ReadAllTextAsync(path));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [TestMethod]
    public void SuccessfulLoadCancelsOldRequestAndLateReplyCannotChangeRestoredState()
    {
        var (project, scene, _, _) = Fixture();
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        var restored = player.State with { NextActionId = scene.Actions[^1].Id };
        GameSave.Load(player, JsonSerializer.Serialize(new { Version = 1, State = restored }));
        var loaded = player.State;
        Assert.IsTrue(runner.Token.IsCancellationRequested);
        Assert.IsFalse(player.HasPendingLua);
        runner.Complete(new SetAction(Guid.NewGuid(), "stale", "bad"));
        Assert.IsFalse(PollReady(player));
        Assert.AreSame(loaded, player.State);
        Assert.IsFalse(player.State.Variables.ContainsKey("stale"));
        Assert.AreEqual(AudioEventKind.Restore, player.DrainAudioEvents().Single().Kind);
        Assert.AreEqual(0d, player.State.Time);
    }

    [TestMethod]
    public void FailedLoadPreservesLiveRequestAndItsResultCanStillCommit()
    {
        var (project, scene, _, _) = Fixture();
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        var before = player.State;
        Assert.Throws<ArgumentException>(() => player.Restore(before with { Revision = "wrong" }));
        Assert.Throws<ArgumentException>(() => GameSave.Load(player, "not-json"));
        Assert.AreSame(before, player.State);
        Assert.IsFalse(runner.Token.IsCancellationRequested);
        runner.Complete(new SetAction(Guid.NewGuid(), "live", "yes"));
        PollReady(player); Assert.AreEqual("yes", player.State.Variables["live"]);
    }

    [TestMethod]
    [DataRow("error")]
    [DataRow("cancelled")]
    [DataRow("duplicate")]
    [DataRow("missing-object")]
    [DataRow("too-many")]
    public void FailedOrInvalidResultKeepsActionAndDiscardsWholeBatch(string kind)
    {
        var (project, scene, script, _) = Fixture();
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        player.DrainAudioEvents();
        var set = new SetAction(Guid.NewGuid(), "partial", "bad");
        switch (kind)
        {
            case "error": runner.Completion.SetException(new InvalidOperationException("worker failed")); break;
            case "cancelled": runner.Completion.SetCanceled(); break;
            case "duplicate": runner.Complete(set, set); break;
            case "missing-object": runner.Complete(set, new ShowAction(Guid.NewGuid(), Guid.NewGuid())); break;
            default: runner.Completion.SetResult([.. Enumerable.Range(0, 257).Select(_ => (ScenarioAction)new SetAction(Guid.NewGuid(), "x", "y"))]); break;
        }
        PollReady(player);
        Assert.AreEqual(script.Id, player.State.NextActionId);
        Assert.IsEmpty(player.State.Variables);
        Assert.IsEmpty(player.State.QueuedActions);
        Assert.IsNotNull(player.State.Error);
        Assert.AreEqual(AudioEventKind.Stop, player.DrainAudioEvents().Single().Kind);
    }

    [TestMethod]
    public void CancelAndDisposeInvalidateOwnershipWithoutApplyingLateReply()
    {
        var (project, scene, _, _) = Fixture();
        foreach (var dispose in new[] { false, true })
        {
            var runner = new ControlledRunner();
            using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
            if (dispose) player.Dispose(); else player.Cancel();
            Assert.IsTrue(runner.Token.IsCancellationRequested);
            var state = player.State;
            runner.Complete(new SetAction(Guid.NewGuid(), "late", "bad"));
            Assert.IsFalse(PollReady(player));
            player.Tick(TimeSpan.FromSeconds(20));
            Assert.AreSame(state, player.State);
            Assert.IsFalse(player.Advance());
        }
    }

    [TestMethod]
    public void ImmediateAsyncResultsStillHaveABoundedLogicalPassAcrossHostFrames()
    {
        var (project, scene, script, _) = Fixture();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", (_, _, _, _) =>
            Task.FromResult<ImmutableArray<ScenarioAction>>([new JumpAction(Guid.NewGuid(), scene.Id, script.Id)]));
        for (var i = 0; i < 40 && player.HasPendingLua; i++) PollReady(player);
        Assert.IsNotNull(player.State.Error);
        StringAssert.Contains(player.State.Error, "лимит действий Lua");
    }

    [TestMethod]
    public async Task DefaultRunnerReallyIsAsyncAndExplicitHostPollCommitsValidatedCommands()
    {
        var (project, scene, script, _) = Fixture();
        scene = scene with { Actions = scene.Actions.SetItem(0, script with { Source = "game.set('actual', 'yes')" }) };
        await using var player = new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru");
        Assert.IsTrue(player.HasPendingLua);
        await player.PendingLuaTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsEmpty(player.State.Variables);
        player.Tick(TimeSpan.FromSeconds(3));
        Assert.AreEqual("yes", player.State.Variables["actual"]);
        Assert.AreEqual(0d, player.State.Time);
    }

    [TestMethod]
    public async Task AsyncDisposalAwaitsCancelledWorkEvenAfterOrdinaryDispose()
    {
        var (project, scene, _, _) = Fixture();
        var runner = new ControlledRunner();
        var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        player.Dispose();
        var cleanup = player.DisposeAsync().AsTask();
        Assert.IsFalse(cleanup.IsCompleted);
        runner.Completion.SetCanceled();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void SynchronousRunnerFailureRestoresPositionAndBecomesDiagnostic()
    {
        var (project, scene, script, _) = Fixture();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", (_, _, _, _) => throw new InvalidOperationException("runner refused")).Settled();
        Assert.AreEqual(script.Id, player.State.NextActionId);
        Assert.IsFalse(player.HasPendingLua);
        StringAssert.Contains(player.State.Error!, "runner refused");
    }

    [TestMethod]
    public void ResultThatFitsCommandCountButExceedsQueueCharactersIsRejected()
    {
        var (project, scene, script, _) = Fixture();
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", runner.Run);
        runner.Complete([.. Enumerable.Range(0, 65).Select(_ => (ScenarioAction)new HotspotAction(Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 8192), 0.1, 0.1, 0.1, 0.1))]);
        PollReady(player);
        Assert.AreEqual(script.Id, player.State.NextActionId);
        StringAssert.Contains(player.State.Error!, "размер очереди Lua");
        Assert.IsEmpty(player.State.Hotspots);
    }

    [TestMethod]
    public async Task RetiredFailuresAreObservedAndAsyncCleanupIsBoundedForUncooperativeRunner()
    {
        var (project, scene, _, _) = Fixture();
        var first = new ControlledRunner(); var second = new ControlledRunner(); var calls = 0;
        var player = new ScenarioPlayer(project, scene.Id, "ru", (a, s, p, token) =>
            ++calls == 1 ? first.Run(a, s, p, token) : second.Run(a, s, p, token));
        var restored = player.State;
        player.Restore(restored);
        first.Completion.SetException(new InvalidOperationException("old failure"));
        player.Tick(TimeSpan.FromSeconds(0.1));
        Assert.IsTrue(player.HasPendingLua);
        player.Dispose();
        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        second.Completion.SetException(new InvalidOperationException("late failure"));
        Assert.IsFalse(player.HasPendingLua); Assert.IsNull(player.State.Error);
    }

    [TestMethod]
    public void IntermediateVariableOverflowRejectsEntireBatchEvenIfFinalValuesWouldFit()
    {
        var (project, scene, script, _) = Fixture();
        var initial = Enumerable.Range(0, 16).Select(i => (ScenarioAction)new SetAction(Guid.NewGuid(), i.ToString("D2"), new string('x', i == 15 ? 8160 : 8192)));
        scene = scene with { Actions = [.. initial, .. scene.Actions] };
        var runner = new ControlledRunner();
        using var player = new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru", runner.Run);
        var before = player.State;
        runner.Complete(new SetAction(Guid.NewGuid(), "15", new string('x', 8161)), new SetAction(Guid.NewGuid(), "15", "small"));
        PollReady(player);
        Assert.AreEqual(script.Id, player.State.NextActionId);
        Assert.AreSame(before.Variables, player.State.Variables);
        StringAssert.Contains(player.State.Error!, "бюджет переменных");
    }

    [TestMethod]
    public void InteractionVariableCannotExceedBudgetAfterRestoreAndDoesNotCrashHost()
    {
        var (project, scene, _, _) = Fixture();
        var hotspot = Guid.NewGuid();
        scene = scene with { Actions = [new HotspotAction(Guid.NewGuid(), hotspot, "Click", 0.1, 0.1, 0.1, 0.1),
            new WaitAction(Guid.NewGuid(), "interaction")] };
        using var player = new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru");
        var variables = Enumerable.Range(0, 256).ToImmutableDictionary(i => "v" + i, _ => "value");
        player.Restore(player.State with { Variables = variables });
        Assert.IsFalse(player.Click(hotspot));
        Assert.AreSame(variables, player.State.Variables);
        StringAssert.Contains(player.State.Error!, "бюджет переменных");
    }

    [TestMethod]
    public void EvenAnEmptyBatchCannotValidateAnAlreadyInvalidVariableSnapshot()
    {
        var (project, scene, _, _) = Fixture();
        using var player = new ScenarioPlayer(project, scene.Id, "ru", new ControlledRunner().Run);
        var invalid = player.State with { Variables = Enumerable.Range(0, 257).ToImmutableDictionary(i => "v" + i, _ => "x") };
        Assert.Throws<InvalidOperationException>(() => LuaActionScript.ValidateCommands([], invalid, project));
    }

    private static bool PollReady(ScenarioPlayer player)
    {
        if (player.PendingLuaTask is { } work)
        {
            try { work.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException) { }
        }
        return player.PollLua();
    }

    private sealed class ControlledRunner
    {
        public TaskCompletionSource<ImmutableArray<ScenarioAction>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<ImmutableArray<ScenarioAction>> Run(ScriptAction action, GameState state, NovelProject project, CancellationToken token)
        { Token = token; return Completion.Task; }
        public void Complete(params ScenarioAction[] commands) => Completion.SetResult([.. commands]);
    }

    private static (NovelProject Project, NovelScene Scene, ScriptAction Script, NovelCharacter Actor) Fixture(bool motion = false)
    {
        var image = new ImageAsset(Guid.NewGuid(), "Actor", "Assets/Images/actor.png", 200, 400);
        var pose = new CharacterState(Guid.NewGuid(), "Idle", image.Id);
        var actor = NovelCharacter.Create("Actor") with { States = [pose], DefaultStateId = pose.Id };
        var script = new ScriptAction(Guid.NewGuid(), "-- test", "pending.lua");
        var scene = NovelScene.Create("Scene") with { UseActions = true, Actions = [script, new WaitAction(Guid.NewGuid(), "input")] };
        if (motion) scene = scene with { Actions = [new PlaceAction(Guid.NewGuid(), actor.Id, new(0.2, 1, 1, 0)),
            new MoveAction(Guid.NewGuid(), actor.Id, new(0.8, 1, 1, 0), 4), .. scene.Actions] };
        return (NovelProject.Create("Pending") with { Scenes = [scene], Images = [image], Characters = [actor] }, scene, script, actor);
    }
}
