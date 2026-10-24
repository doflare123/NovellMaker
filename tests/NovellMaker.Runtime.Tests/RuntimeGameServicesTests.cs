using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeGameServicesTests
{
    [TestMethod]
    public async Task CorruptSlotWithEmptyLineIdIsReportedByPollAlongsideHealthySlot()
    {
        var path = Path.Combine(TestPaths.TempDirectory, "slot-service-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Project(); using var session = new GameSession(project, project.Scenes[0].Id, "ru");
            var store = new GameSlotStore(project, Path.Combine(path, "slots"), Path.Combine(path, "quick.json"));
            var snapshot = GameSlotStore.Prepare(GameSlotStore.Capture(session));
            await store.SaveAsync("broken", snapshot); await store.SaveAsync("healthy", snapshot);
            var file = Path.Combine(path, "slots", "broken.json"); var document = JsonNode.Parse(File.ReadAllText(file))!;
            var state = JsonNode.Parse(snapshot.SaveJson)!; state["State"]!["LineId"] = Guid.Empty;
            document["Metadata"]!["LineId"] = Guid.Empty; document["SaveJson"] = state.ToJsonString();
            File.WriteAllText(file, document.ToJsonString());
            await using var slots = new GameSessionSlots(session, store); using var services = new RuntimeGameServices(session, slots);
            GameServiceResult? delivered = null; services.Completed += (_, result) => delivered = result;
            Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.ListSlots), session.Generation));
            await Drain(services, slots); Assert.IsNotNull(delivered); Assert.IsTrue(delivered.Success);
            Assert.HasCount(2, delivered.Slots); var broken = delivered.Slots.Single(s => s.Id == "broken");
            Assert.AreEqual(GameSlotStatus.Corrupt, broken.Status); Assert.IsNull(broken.Metadata);
            Assert.IsFalse(string.IsNullOrEmpty(broken.Diagnostic));
            Assert.AreEqual(GameSlotStatus.Compatible, delivered.Slots.Single(s => s.Id == "healthy").Status);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [TestMethod]
    public async Task ClosingOriginBeforePollCancelsEvenCompletedLoadWithoutReplacingState()
    {
        var project = Project(); using var session = new GameSession(project, project.Scenes[0].Id, "ru"); var store = new Store();
        var snapshot = GameSlotStore.Prepare(GameSlotStore.Capture(session)); var state = session.Player.Execution.State;
        await using var slots = new GameSessionSlots(session, store); using var services = new RuntimeGameServices(session, slots);
        var stack = new ScreenStack(Guid.NewGuid()); var origin = stack.Open(Guid.NewGuid(), ScreenFixture.Create("UI").Definition());
        services.IsCurrentOrigin = candidate => stack.Instances.Any(i => ReferenceEquals(i,candidate));
        var replies = 0; services.Completed += (_, _) => replies++;
        Assert.IsTrue(services.Request(origin,new(Guid.NewGuid(),GameServiceAction.LoadSlot,"slot"),0));
        store.Read.SetResult(new(new("slot",GameSlotStatus.Compatible,snapshot.Metadata,null),snapshot));
        await Task.Delay(10); stack.Close(origin); Assert.IsFalse(services.Poll());
        Assert.IsTrue(store.Token.IsCancellationRequested); Assert.AreSame(state,session.Player.Execution.State);
        Assert.AreEqual(0L,session.Generation); Assert.AreEqual(0,replies); Assert.IsFalse(slots.IsBusy);
        Assert.IsFalse(services.Request(origin,new(Guid.NewGuid(),GameServiceAction.ListSlots),0));
    }

    private static NovelProject Project() => NovelProject.Create("Сервисы") with { Scenes = [NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Фраза")] }] };
    private sealed class Store : IGameSlotStore
    {
        internal readonly TaskCompletionSource<GameSlotRead> Read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        internal string? SavedId; internal GameSlotSnapshot? Snapshot;
        internal ImmutableArray<GameSlotInfo> Items = [];
        public Task SaveAsync(string id, GameSlotSnapshot snapshot, CancellationToken cancellationToken = default)
        { SavedId = id; Snapshot = snapshot; Token = cancellationToken; SaveStarted.TrySetResult(); return Saved.Task; }
        public Task<GameSlotRead> ReadAsync(string id, CancellationToken cancellationToken = default) { Token = cancellationToken; return Read.Task; }
        public async Task<GameSlotRead?> LatestAsync(CancellationToken cancellationToken = default) => await ReadAsync("latest", cancellationToken);
        public Task<ImmutableArray<GameSlotInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private static async Task Drain(RuntimeGameServices services, GameSessionSlots slots)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (slots.IsBusy) { timeout.Token.ThrowIfCancellationRequested(); services.Poll(); await Task.Delay(1); }
    }

    [TestMethod]
    public async Task SaveUsesGeneratedIdAndReturnsCorrelatedResultToOriginWhileBusyPreservesFirstRequest()
    {
        var project = Project(); using var session = new GameSession(project, project.Scenes[0].Id, "ru"); var store = new Store();
        await using var slots = new GameSessionSlots(session, store); using var services = new RuntimeGameServices(session, slots);
        var stack = new ScreenStack(Guid.NewGuid()); var origin = stack.Open(Guid.NewGuid(), ScreenFixture.Create("UI").Definition());
        var first = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.SaveSlot, Title: "Глава");
        var replies = new List<(ScreenInstance? Origin, GameServiceResult Result)>(); services.Completed += (owner, result) => replies.Add((owner, result));
        Assert.IsTrue(services.Request(origin, first, session.Generation));
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(GameSlotStore.IsValidSlotId(store.SavedId));
        Assert.AreEqual("Глава", store.Snapshot!.Metadata.Title);
        var second = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.ListSlots);
        Assert.IsFalse(services.Request(origin, second, session.Generation)); Assert.IsTrue(slots.IsBusy);
        Assert.AreEqual(second.RequestId, replies.Single().Result.RequestId); Assert.IsFalse(replies[0].Result.Success);
        store.Saved.SetResult(); await Drain(services, slots);
        Assert.AreEqual(first.RequestId, replies[1].Result.RequestId); Assert.AreEqual(store.SavedId, replies[1].Result.SlotId);
        Assert.AreSame(origin, replies[1].Origin); Assert.IsTrue(replies[1].Result.Success);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task NewGameCancelsPendingLoadAndLateReplyCannotEnterNewLifetime(bool fault)
    {
        var project = Project(); project = project with { StartScreenSceneId = project.Scenes[0].Id };
        using var seed = new GameSession(project, project.Scenes[0].Id, "ru"); seed.NewGame(0); var snapshot = GameSlotStore.Prepare(GameSlotStore.Capture(seed));
        using var session = new GameSession(project, project.Scenes[0].Id, "ru"); var store = new Store();
        await using var slots = new GameSessionSlots(session, store); using var services = new RuntimeGameServices(session, slots);
        var replies = 0; services.Completed += (_, _) => replies++;
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.LoadSlot, "old"), 0));
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.NewGame), 0)); Assert.IsTrue(store.Token.IsCancellationRequested);
        var current = session.Player; var state = current.Execution.State;
        if (fault) store.Read.SetException(new IOException("late fault"));
        else store.Read.SetResult(new(new("old", GameSlotStatus.Compatible, snapshot.Metadata, null), snapshot));
        await Task.Delay(10); Assert.IsFalse(services.Poll()); Assert.AreSame(current, session.Player); Assert.AreSame(state, current.Execution.State);
        Assert.AreEqual(0, replies); Assert.IsFalse(services.Request(null, new(Guid.NewGuid(), GameServiceAction.MainMenu), 0));
    }

    [TestMethod]
    public async Task SuccessfulLoadResetsOriginAndDoesNotSendResultIntoReplacementHandler()
    {
        var project = Project(); using var session = new GameSession(project, project.Scenes[0].Id, "ru");
        var snapshot = GameSlotStore.Prepare(GameSlotStore.Capture(session)); var store = new Store();
        await using var slots = new GameSessionSlots(session, store); using var services = new RuntimeGameServices(session, slots);
        var replies = 0; services.Completed += (_, _) => replies++;
        var request = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.LoadSlot, "slot"); Assert.IsTrue(services.Request(null, request, 0));
        store.Read.SetResult(new(new("slot", GameSlotStatus.Compatible, snapshot.Metadata, null), snapshot)); await Drain(services, slots);
        Assert.AreEqual(1L, session.Generation); Assert.AreEqual(0, replies); Assert.IsTrue(services.LastResult!.Success);
        Assert.AreEqual(request.RequestId, services.LastResult.RequestId); Assert.AreEqual(snapshot.SaveJson, GameSave.Serialize(session.Player.Execution));
    }

    [TestMethod]
    public async Task SessionCommandsAndEmptyListOrDeleteProduceDefinedResults()
    {
        var project = Project(); project = project with { StartScreenSceneId = project.Scenes[0].Id };
        using var session = new GameSession(project, project.Scenes[0].Id, "ru"); var store = new Store();
        await using var slots = new GameSessionSlots(session, store); using var services = new RuntimeGameServices(session, slots);
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.ListSlots), 0)); await Drain(services, slots);
        Assert.IsTrue(services.LastResult!.Success); Assert.IsEmpty(services.LastResult.Slots);
        Assert.IsFalse(services.Request(null, new(Guid.NewGuid(), GameServiceAction.SaveSlot, "slot"), 0)); Assert.IsFalse(services.LastResult!.Success);
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.NewGame), 0));
        Assert.IsFalse(services.Request(null, new(Guid.NewGuid(), GameServiceAction.NewGame), 1)); Assert.IsFalse(services.LastResult!.Success);
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.DeleteSlot, "slot"), 1)); await Drain(services, slots);
        Assert.IsTrue(services.LastResult!.Success);
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.MainMenu), 1));
        Assert.IsTrue(services.Request(null, new(Guid.NewGuid(), GameServiceAction.EndSession), 2)); Assert.AreEqual(GameSessionPhase.Ended, session.Phase);
    }

    [TestMethod]
    public async Task FileErrorsAndUnavailableRowsDoNotExposeProfilePathsInLuaResult()
    {
        var project = Project(); using var session = new GameSession(project,project.Scenes[0].Id,"ru"); var store = new Store();
        await using var slots = new GameSessionSlots(session,store); using var services = new RuntimeGameServices(session,slots);
        const string path = "C:\\Users\\PrivateName\\AppData\\Local\\NovellMaker\\Games\\slot.json";
        Assert.IsTrue(services.Request(null,new(Guid.NewGuid(),GameServiceAction.LoadSlot,"slot"),0));
        store.Read.SetResult(new(new("slot",GameSlotStatus.Unavailable,null,"Access denied: "+path),null)); await Drain(services,slots);
        Assert.IsFalse(services.LastResult!.Success); Assert.IsFalse(services.LastResult.Message.Contains("PrivateName",StringComparison.Ordinal));
        store.Items = [new("slot",GameSlotStatus.Unavailable,null,"Access denied: "+path)];
        Assert.IsTrue(services.Request(null,new(Guid.NewGuid(),GameServiceAction.ListSlots),0)); await Drain(services,slots);
        Assert.AreEqual("Файл слота недоступен.",services.LastResult!.Slots[0].Diagnostic);
    }

    [TestMethod]
    public async Task ManifestV2StrictlyMatchesProjectAndStillReadsLegacyV1()
    {
        var project = Project(); var path = Path.Combine(Path.GetTempPath(), "manifest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var package = new GamePackage(2, project.Id, project.Scenes[0].Id, "ru", project.Name) { StartScreenSceneId = project.Scenes[0].Id };
            await package.SaveAsync(path); var loaded = GamePackage.Load(path); Assert.AreEqual(package, loaded);
            Assert.Throws<InvalidDataException>(() => loaded.ValidateProject(project)); loaded.ValidateProject(project with { StartScreenSceneId = project.Scenes[0].Id });
            var file = Path.Combine(path, "game.json"); var valid = File.ReadAllText(file);
            foreach (var field in new[] { "StartScreenSceneId", "SaveContainerVersion" })
            { var doc = JsonNode.Parse(valid)!; doc.AsObject().Remove(field); File.WriteAllText(file, doc.ToJsonString()); Assert.Throws<InvalidDataException>(() => GamePackage.Load(path)); }
            var invalid = JsonNode.Parse(valid)!; invalid["SaveContainerVersion"] = 100; File.WriteAllText(file, invalid.ToJsonString()); Assert.Throws<InvalidDataException>(() => GamePackage.Load(path));
            invalid = JsonNode.Parse(valid)!; invalid["Version"] = 1; File.WriteAllText(file, invalid.ToJsonString()); Assert.Throws<Exception>(() => GamePackage.Load(path));
            var legacy = new GamePackage(1, project.Id, project.Scenes[0].Id, "ru", project.Name); await legacy.SaveAsync(path);
            Assert.AreEqual(legacy, GamePackage.Load(path)); GamePackage.Load(path).ValidateProject(project);
        }
        finally { Directory.Delete(path, true); }
    }
}
