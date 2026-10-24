using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class PreparedGameLoadTests
{
    private static NovelProject Project() => NovelProject.Create("Prepare load") with { Scenes = [NovelScene.Create("Scene") with {
        Lines = [DialogueLine.Create("ru", "First"), DialogueLine.Create("ru", "Second")] }] };

    [TestMethod]
    public async Task PreparationPreservesLiveLifetimeAndCommitRestoresQueuedLuaWithoutRunningIt()
    {
        var project = Project(); var calls = 0;
        using var session = new GameSession(project, project.Scenes[0].Id, "ru", null, PlaybackMode.Game,
            (_, _, _, _) => { calls++; return Task.FromResult(ImmutableArray<ScenarioAction>.Empty); });
        var capture = GameSave.Capture(session.Player.Execution);
        var json = GameSave.Serialize(capture with { State = capture.State with {
            QueuedActions = [new ScriptAction(Guid.NewGuid(), "game.set('loaded','yes')", "queued")], Time = 12, TextStartedAt = 12 } });
        var old = session.Player; var oldState = old.Execution.State; var resets = 0;
        session.Resetting += () => resets++;
        using var prepared = await session.CaptureLoadPreparation().PrepareAsync(json, CancellationToken.None);
        Assert.IsTrue(prepared.IsAvailable); Assert.AreSame(old, session.Player); Assert.AreSame(oldState, session.Player.Execution.State);
        Assert.AreEqual(0, resets); Assert.AreEqual(0, calls); Assert.AreEqual(0L, session.Generation);
        Assert.IsTrue(session.TryLoadPrepared(prepared, 0));
        Assert.IsFalse(prepared.IsAvailable); Assert.AreEqual(1, resets); Assert.AreEqual(1L, session.Generation);
        Assert.AreEqual(json, GameSave.Serialize(session.Player.Execution)); Assert.AreEqual(12d, session.Player.Execution.State.Time);
        Assert.AreEqual(0, calls); Assert.IsFalse(session.Player.Execution.HasPendingLua);
        Assert.IsTrue(session.Player.Next()); Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task ForeignOwnerAndSecondUseCannotPublishEvenWhenProjectAndGenerationMatch()
    {
        var project = Project(); using var first = new GameSession(project, project.Scenes[0].Id, "ru");
        using var second = new GameSession(project, project.Scenes[0].Id, "ru"); var other = second.Player;
        using var prepared = await first.CaptureLoadPreparation().PrepareAsync(GameSave.Serialize(first.Player.Execution), CancellationToken.None);
        Assert.Throws<ArgumentException>(() => second.TryLoadPrepared(prepared, 0));
        Assert.AreSame(other, second.Player); Assert.AreEqual(0L, second.Generation); Assert.IsTrue(prepared.IsAvailable);
        Assert.IsTrue(first.TryLoadPrepared(prepared, 0)); var current = first.Player;
        Assert.Throws<InvalidOperationException>(() => first.TryLoadPrepared(prepared, first.Generation));
        Assert.AreSame(current, first.Player); Assert.AreEqual(1L, first.Generation);
    }

    [TestMethod]
    public async Task CapturedPreparationNeverNeedsDisposedSessionAndStaleResultCanBeDisposed()
    {
        var project = Project(); var session = new GameSession(project, project.Scenes[0].Id, "ru");
        var context = session.CaptureLoadPreparation(); var json = GameSave.Serialize(session.Player.Execution);
        session.Dispose(); using var prepared = await context.PrepareAsync(json, CancellationToken.None);
        Assert.IsFalse(session.TryLoadPrepared(prepared, 0)); Assert.IsTrue(prepared.IsAvailable);
        prepared.Dispose(); Assert.IsFalse(prepared.IsAvailable);
    }

    [TestMethod]
    public async Task CancellationAndInvalidJsonDoNotReplacePlayerOrRunCleanup()
    {
        var project = Project(); using var session = new GameSession(project, project.Scenes[0].Id, "ru");
        var old = session.Player; var resets = 0; session.Resetting += () => resets++;
        var context = session.CaptureLoadPreparation();
        await Assert.ThrowsAsync<OperationCanceledException>(() => context.PrepareAsync(GameSave.Serialize(old.Execution), new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => context.PrepareAsync("broken", CancellationToken.None));
        Assert.AreSame(old, session.Player); Assert.AreEqual(0, resets); Assert.AreEqual(0L, session.Generation);
        using var valid = await context.PrepareAsync(GameSave.Serialize(old.Execution), CancellationToken.None);
        Assert.IsTrue(session.TryLoadPrepared(valid, 0));
    }
}
