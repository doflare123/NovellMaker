using System.Collections.Immutable;
using System.Text.Json;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class LuaInvocationSnapshotTests
{
    [TestMethod]
    public void IndexedCompilationPreservesPublishedCueIdsAndOrderOfInterleavedEvents()
    {
        var first = DialogueLine.Create("ru", "first") with { Id = Guid.Parse("11111111-1111-1111-1111-111111111111") };
        var second = DialogueLine.Create("ru", "second") with { Id = Guid.Parse("22222222-2222-2222-2222-222222222222") };
        var start = new SetAction(Guid.NewGuid(), "start", "yes"); var end = new SetAction(Guid.NewGuid(), "end", "yes");
        var before = new SetAction(Guid.NewGuid(), "before", "1"); var beforeAgain = new SetAction(Guid.NewGuid(), "before", "2");
        var during = new SetAction(Guid.NewGuid(), "during", "yes"); var after = new SetAction(Guid.NewGuid(), "after", "yes");
        var next = new SetAction(Guid.NewGuid(), "next", "yes");
        var scene = NovelScene.Create("order") with { Lines = [first, second], ScriptBlocks = [
            new(second.Id, ScriptPhase.BeforeLine, next), new(first.Id, ScriptPhase.BeforeLine, before),
            new(null, ScriptPhase.SceneEnd, end), new(first.Id, ScriptPhase.AfterLine, after),
            new(null, ScriptPhase.SceneStart, start), new(first.Id, ScriptPhase.DuringLine, during),
            new(first.Id, ScriptPhase.BeforeLine, beforeAgain)] };
        Guid[] expected = [start.Id, Guid.Parse("2c3ce032-62b0-e7b4-b60c-7ecb4668bdb7"), before.Id, beforeAgain.Id,
            Guid.Parse("fd81de4b-d6d2-6b97-133e-0a6260e126fb"), during.Id, Guid.Parse("dee198f6-4286-9b1d-cfe2-14ce31598d02"), after.Id,
            Guid.Parse("6f6812b1-579e-b3c3-126e-e5282bb75342"), next.Id, Guid.Parse("e8020eeb-b382-94c9-8945-13e08a374996"),
            Guid.Parse("2262084e-3f32-ca32-e915-d4581055082d"), end.Id];
        CollectionAssert.AreEqual(expected, ScenarioProgram.Compile(scene).Select(a => a.Id).ToArray());
        var project = NovelProject.Create("order") with { Scenes = [scene] };
        CollectionAssert.AreEqual(expected, LuaCommandCatalog.Capture(project, scene.Id).SceneActions[scene.Id].ToArray());
    }

    internal static NovelProject CatalogFixture()
    {
        var lines = Enumerable.Range(0, 100).Select(_ => DialogueLine.Create("ru", "line")).ToImmutableArray();
        var scene = NovelScene.Create("catalog") with { Lines = lines,
            ScriptBlocks = [.. lines.Select(l => new ScriptBlock(l.Id, ScriptPhase.BeforeLine, new SetAction(Guid.NewGuid(), "v", "yes")))] };
        return NovelProject.Create("catalog") with { Scenes = [scene] };
    }

    [TestMethod]
    public async Task CatalogIsSharedAcrossScenesAndConcurrentRequestsButNewProjectIdentityInvalidatesIt()
    {
        var project = CatalogFixture(); var first = project.Scenes[0];
        var second = NovelScene.Create("second") with { Lines = [DialogueLine.Create("ru", "other")] };
        project = project with { Scenes = [first, second] };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => LuaCommandCatalog.Capture(project, first.Id))));
        Assert.IsTrue(results.All(c => ReferenceEquals(results[0], c)));
        var other = LuaCommandCatalog.Capture(project, second.Id);
        Assert.AreSame(results[0].SceneActions, other.SceneActions); Assert.AreSame(results[0].ObjectStates, other.ObjectStates);
        CollectionAssert.AreEqual(new[] { second.Lines[0].Id }, other.Lines.ToArray());
        var equalCopy = project with { };
        Assert.AreNotSame(results[0], LuaCommandCatalog.Capture(equalCopy, first.Id));
        var newLine = DialogueLine.Create("ru", "new");
        var edited = project with { Scenes = [first with { Lines = first.Lines.Add(newLine) }, second] };
        var updated = LuaCommandCatalog.Capture(edited, first.Id);
        Assert.IsTrue(updated.Lines.Contains(newLine.Id)); Assert.IsFalse(results[0].Lines.Contains(newLine.Id));
        Assert.IsTrue(updated.SceneActions[first.Id].Contains(ScenarioProgram.LineEntryId(newLine.Id)));
        Assert.AreSame(results[0], LuaCommandCatalog.Capture(project, first.Id));
    }

    [TestMethod]
    [DataRow(10)][DataRow(150)][DataRow(260)]
    public void CancellationDuringCatalogLoopsOrCompilationStopsBuildAndDoesNotCachePartialResult(int cancelAt)
    {
        var project = CatalogFixture(); var scene = project.Scenes[0];
        using var cancellation = new CancellationTokenSource(); var checkpoints = 0;
        var preparation = new LuaRequestPreparation(cancellation.Token, () =>
        {
            if (++checkpoints == cancelAt) cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });
        var error = Assert.Throws<OperationCanceledException>(() => LuaCommandCatalog.Capture(project, scene.Id, preparation));
        Assert.AreEqual(cancellation.Token, error.CancellationToken); Assert.AreEqual(cancelAt, checkpoints);
        var retry = LuaCommandCatalog.Capture(project, scene.Id);
        CollectionAssert.AreEqual(ScenarioProgram.Compile(scene).Select(a => a.Id).ToArray(), retry.SceneActions[scene.Id].ToArray());
        Assert.AreSame(retry, LuaCommandCatalog.Capture(project, scene.Id));
        Assert.Throws<OperationCanceledException>(() => LuaCommandCatalog.Capture(project, scene.Id, cancellation.Token));
    }

    [TestMethod]
    public async Task WaitingForSharedCatalogCanBeCancelledWithoutCancellingOrDuplicatingItsBuilder()
    {
        var project = CatalogFixture(); var scene = project.Scenes[0];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); using var cancellation = new CancellationTokenSource();
        var checks = 0;
        var builder = Task.Run(() => LuaCommandCatalog.Capture(project, scene.Id, new LuaRequestPreparation(CancellationToken.None, () =>
        {
            if (Interlocked.Increment(ref checks) != 3) return;
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Builder was not released.");
        })));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var waiter = Task.Run(() => LuaCommandCatalog.Capture(project, scene.Id, new LuaRequestPreparation(cancellation.Token, () =>
            { cancellation.Token.ThrowIfCancellationRequested(); waiting.TrySetResult(); })));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsFalse(builder.IsCompleted);
        }
        finally { release.Set(); }
        var result = await builder.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreSame(result, LuaCommandCatalog.Capture(project, scene.Id));
    }

    [TestMethod]
    public async Task NovelOverWireLimitCanRunSmallLuaAndRequestContainsNoDialogueOrOtherSources()
    {
        var scene = NovelScene.Create("snapshot");
        var project = NovelProject.Create("large") with { Scenes = [scene] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru");
        var state = player.State;
        // The same resource IDs and source length, radically different payload size.
        var script = new LuaScriptAsset(Guid.NewGuid(), "unrelated.lua", "-- PRIVATE_SOURCE");
        project = project with { Scripts = [script] };
        var largeScene = scene with { Lines = [.. Enumerable.Range(0, 600).Select(_ => DialogueLine.Create("ru", new string('x', 32768)))] };
        project = project with { Scenes = [largeScene] };
        var action = new ScriptAction(Guid.NewGuid(), "game.set('result', 'yes')", "small.lua");
        var request = new LuaWorkerRequest("run", action.Source, action.Name, action,
            LuaStateSnapshot.Capture(state), LuaCommandCatalog.Capture(project, state.SceneId));
        var json = JsonSerializer.Serialize(request);
        Assert.IsLessThan(1024 * 1024, json.Length);
        Assert.IsFalse(json.Contains("PRIVATE_SOURCE")); Assert.IsFalse(json.Contains(new string('x', 100)));
        // At least 19 MiB of text alone, without serializing it into another huge buffer in the test.
        Assert.IsGreaterThan(LuaWorkerClient.MaximumRequestBytes, largeScene.Lines.Sum(l => l.Texts["ru"].Length));
        var commands = await LuaActionScript.RunAsync(action, state, project);
        Assert.AreEqual("yes", ((SetAction)commands.Single()).Value);
    }

    [TestMethod]
    public void CatalogUsesSameActionValidationAndStateSnapshotOmitsHostOnlyQueues()
    {
        var scene = NovelScene.Create("catalog") with { Lines = [DialogueLine.Create("ru", "line")] };
        var project = NovelProject.Create("catalog") with { Scenes = [scene] };
        using var player = new ScenarioPlayer(project, scene.Id, "ru");
        var catalog = LuaCommandCatalog.Capture(project, scene.Id);
        ScenarioAction[] actions = [new TextAction(Guid.NewGuid(), scene.Lines[0].Id),
            new TextAction(Guid.NewGuid(), Guid.NewGuid()), new JumpAction(Guid.NewGuid(), scene.Id, ScenarioProgram.LineEntryId(scene.Lines[0].Id)),
            new JumpAction(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), new RunScriptAction(Guid.NewGuid(), Guid.NewGuid()),
            new ShowAction(Guid.NewGuid(), Guid.NewGuid()), new StateAction(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new WaitAction(Guid.NewGuid(), "input"), new SetAction(Guid.NewGuid(), "x", "yes")];
        foreach (var action in actions) Assert.AreEqual(ScenarioProgram.IsValidAction(project, scene, action), catalog.IsValid(action));
        var state = player.State with { QueuedActions = [new ScriptAction(Guid.NewGuid(), "PRIVATE_QUEUE_SOURCE", "queued.lua")] };
        var json = JsonSerializer.Serialize(LuaStateSnapshot.Capture(state));
        Assert.IsFalse(json.Contains("PRIVATE_QUEUE_SOURCE"));
        Assert.IsFalse(json.Contains("Revision")); Assert.IsFalse(json.Contains("QueuedActions"));
    }
}
