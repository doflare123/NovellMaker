using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class GameSlotTests
{
    private static NovelProject Project() => NovelProject.Create("Новелла") with
    {
        Scenes = [NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Первая фраза"), DialogueLine.Create("ru", "Вторая фраза")] }]
    };
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slots-" + Guid.NewGuid().ToString("N"));
        public string Slots => System.IO.Path.Combine(Path, "slots");
        public string Quick => System.IO.Path.Combine(Path, "quick.json");
        public Folder() => Directory.CreateDirectory(Path);
        public GameSlotStore Store(NovelProject project) => new(project, Slots, Quick);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private static GameSession Session(NovelProject project) => new(project, project.Scenes[0].Id, "ru");
    private static async Task Finish(GameSessionSlots slots)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (slots.IsBusy) { timeout.Token.ThrowIfCancellationRequested(); slots.Poll(); await Task.Delay(1); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task EmptyLineIdInSnapshotAndMetadataIsCorruptWithoutMetadata(bool legacy)
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project);
        var store = folder.Store(project); var snapshot = GameSlotStore.Capture(session);
        await store.SaveAsync("broken", snapshot); await store.SaveAsync("healthy", snapshot);
        var path = System.IO.Path.Combine(folder.Slots, "broken.json");
        var state = JsonNode.Parse(snapshot.SaveJson)!; state["State"]!["LineId"] = Guid.Empty;
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["Metadata"]!["LineId"] = Guid.Empty; document["SaveJson"] = state.ToJsonString();
        File.WriteAllText(path, legacy ? state.ToJsonString() : document.ToJsonString());
        var read = await store.ReadAsync("broken");
        Assert.AreEqual(GameSlotStatus.Corrupt, read.Info.Status); Assert.IsNull(read.Info.Metadata);
        Assert.IsNull(read.Snapshot); Assert.IsFalse(string.IsNullOrEmpty(read.Info.Diagnostic));
        var list = await store.ListAsync(); Assert.HasCount(2, list);
        new GameServiceResult(Guid.NewGuid(), GameServiceAction.ListSlots, true, "Список", null, list).Validate();
        Assert.AreEqual(snapshot, (await store.LatestAsync())!.Snapshot);
    }

    [TestMethod]
    [DataRow("directory")] [DataRow("locked")] [DataRow("corrupt")] [DataRow("link")]
    public async Task UnusableQuicksaveDoesNotHideNamedSlotsOrPreventContinue(string failure)
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project);
        var store = folder.Store(project); var snapshot = GameSlotStore.Capture(session);
        await store.SaveAsync("healthy", snapshot);
        FileStream? locked = null;
        try
        {
            if (failure == "directory") Directory.CreateDirectory(folder.Quick);
            else if (failure == "link")
            {
                try { File.CreateSymbolicLink(folder.Quick, System.IO.Path.Combine(folder.Slots, "healthy.json")); }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException)
                { Assert.Inconclusive("Symlink requires host privileges."); }
            }
            else
            {
                File.WriteAllText(folder.Quick, "broken");
                if (failure == "locked")
                {
                    if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows sharing violation fixture.");
                    locked = new(folder.Quick, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
            }
            var list = await store.ListAsync(); Assert.HasCount(2, list);
            var quick = list.Single(s => s.Id == GameSlotStore.QuickSlotId);
            Assert.AreEqual(failure == "corrupt" ? GameSlotStatus.Corrupt : GameSlotStatus.Unavailable, quick.Status);
            Assert.IsNull(quick.Metadata); Assert.IsFalse(string.IsNullOrEmpty(quick.Diagnostic));
            Assert.AreEqual(snapshot, (await store.LatestAsync())!.Snapshot);
            session.Player.Next();
            await using var slots = new GameSessionSlots(session, store);
            Assert.IsTrue(slots.Continue()); await Finish(slots);
            Assert.IsTrue(slots.LastResult!.Success); Assert.AreEqual(snapshot.SaveJson, GameSave.Serialize(session.Player.Execution));
        }
        finally { locked?.Dispose(); if (failure == "link") File.Delete(folder.Quick); }
    }

    [TestMethod]
    public async Task QuicksaveThroughJunctionDoesNotAbortNamedSlotScanOrContinue()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows directory junction fixture.");
        using var folder = new Folder(); var project = Project(); using var session = Session(project);
        var target = System.IO.Path.Combine(folder.Path, "target"); Directory.CreateDirectory(target);
        var junction = System.IO.Path.Combine(folder.Path, "junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode);
        try
        {
            var snapshot = GameSlotStore.Capture(session);
            var outside = System.IO.Path.Combine(target, "quick.json"); File.WriteAllText(outside, snapshot.SaveJson);
            var store = new GameSlotStore(project, folder.Slots, System.IO.Path.Combine(junction, "quick.json"));
            await store.SaveAsync("healthy", snapshot);
            var list = await store.ListAsync(); Assert.HasCount(2, list);
            var quick = list.Single(s => s.Id == GameSlotStore.QuickSlotId);
            Assert.AreEqual(GameSlotStatus.Unavailable, quick.Status); Assert.IsNull(quick.Metadata);
            Assert.AreEqual(snapshot, (await store.LatestAsync())!.Snapshot);
            session.Player.Next(); await using var slots = new GameSessionSlots(session, store);
            Assert.IsTrue(slots.Continue()); await Finish(slots); Assert.IsTrue(slots.LastResult!.Success);
            Assert.AreEqual(snapshot.SaveJson, GameSave.Serialize(session.Player.Execution));
            Assert.AreEqual(snapshot.SaveJson, File.ReadAllText(outside));
        }
        finally { Directory.Delete(junction); }
    }

    [TestMethod]
    public async Task SnapshotRoundTripMetadataListDeleteAndQuicksave()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project);
        session.Player.Tick(TimeSpan.FromSeconds(2));
        var snapshot = GameSlotStore.Capture(session, "Мой слот", DateTimeOffset.Parse("2026-10-22T10:00:00Z"));
        var store = folder.Store(project);
        await store.SaveAsync("slot-1", snapshot); await store.SaveAsync(GameSlotStore.QuickSlotId, snapshot);
        session.Player.Next(); session.Player.Tick(TimeSpan.FromSeconds(3));
        var read = await store.ReadAsync("slot-1"); Assert.AreEqual(snapshot, read.Snapshot);
        Assert.AreEqual(GameSlotStatus.Compatible, read.Info.Status);
        Assert.AreEqual("Первая фраза", read.Info.Metadata!.Phrase); Assert.AreEqual(2d, read.Info.Metadata.GameTime);
        Assert.AreEqual("Мой слот", read.Info.Metadata.Title); Assert.AreEqual("Сцена", read.Info.Metadata.SceneName);
        var list = await store.ListAsync(); Assert.HasCount(2, list); Assert.AreEqual("quicksave", list[0].Id);
        Assert.IsTrue(File.Exists(folder.Quick)); Assert.IsFalse(File.Exists(System.IO.Path.Combine(folder.Slots, "quicksave.json")));
        var generation = session.Generation;
        Assert.IsTrue(session.TryLoadSave(read.Snapshot!.SaveJson, generation));
        Assert.AreEqual(2d, session.Player.Execution.State.Time); Assert.AreEqual(project.Scenes[0].Lines[0].Id, session.Player.Frame.LineId);
        Assert.IsFalse(session.TryLoadSave("broken", generation)); // Stale data isn't even parsed.
        await store.DeleteAsync("slot-1"); await store.DeleteAsync("slot-1");
        Assert.HasCount(1, await store.ListAsync()); Assert.IsTrue(File.Exists(folder.Quick));
        await store.DeleteAsync("quicksave"); Assert.IsEmpty(await store.ListAsync()); Assert.IsNull(await store.LatestAsync());
    }

    [TestMethod]
    public async Task VersionOneQuickslotLoadsAndListsWithoutAnyMigrationWrite()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project);
        var text = GameSave.Serialize(session.Player.Execution); File.WriteAllText(folder.Quick, text);
        var time = DateTime.UtcNow.AddDays(-1); File.SetLastWriteTimeUtc(folder.Quick, time);
        var bytes = File.ReadAllBytes(folder.Quick); var store = folder.Store(project);
        var read = await store.ReadAsync("quicksave"); Assert.IsTrue(read.Info.Legacy);
        Assert.AreEqual(GameSlotStatus.Compatible, read.Info.Status); Assert.AreEqual(text, read.Snapshot!.SaveJson);
        Assert.AreEqual(File.GetLastWriteTimeUtc(folder.Quick), read.Info.Metadata!.SavedAtUtc.UtcDateTime);
        Assert.HasCount(1, await store.ListAsync()); Assert.IsNotNull(await store.LatestAsync());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(folder.Quick)); Assert.AreEqual(time, File.GetLastWriteTimeUtc(folder.Quick));
        Assert.IsFalse(Directory.Exists(folder.Slots));
    }

    [TestMethod]
    public async Task ContinueSkipsNewerForeignRevisionCorruptAndUnsupportedSlots()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project);
        var store = folder.Store(project); var old = GameSlotStore.Capture(session, savedAt: DateTimeOffset.UtcNow.AddDays(-3));
        await store.SaveAsync("old", old); session.Player.Next();
        var latest = GameSlotStore.Capture(session, savedAt: DateTimeOffset.UtcNow.AddDays(-2));
        await store.SaveAsync("latest", latest);
        var changed = project with { Scenes = [project.Scenes[0] with { Name = "Изменённая сцена" }] };
        using var changedSession = Session(changed);
        await folder.Store(changed).SaveAsync("revision", GameSlotStore.Capture(changedSession));
        var foreign = Project(); using var foreignSession = Session(foreign);
        await folder.Store(foreign).SaveAsync("foreign", GameSlotStore.Capture(foreignSession));
        File.WriteAllText(System.IO.Path.Combine(folder.Slots, "corrupt.json"), "{broken");
        File.WriteAllText(System.IO.Path.Combine(folder.Slots, "future.json"), "{\"Version\":999}");
        var list = await store.ListAsync(); Assert.HasCount(6, list);
        Assert.AreEqual(GameSlotStatus.ForeignProject, list.Single(s => s.Id == "foreign").Status);
        Assert.AreEqual(GameSlotStatus.IncompatibleRevision, list.Single(s => s.Id == "revision").Status);
        Assert.AreEqual(GameSlotStatus.Corrupt, list.Single(s => s.Id == "corrupt").Status);
        Assert.AreEqual(GameSlotStatus.UnsupportedVersion, list.Single(s => s.Id == "future").Status);
        Assert.AreEqual(latest, (await store.LatestAsync())!.Snapshot);
        await using var slots = new GameSessionSlots(session, store); Assert.IsTrue(slots.Continue()); await Finish(slots);
        Assert.IsTrue(slots.LastResult!.Success); Assert.AreEqual(latest.SaveJson, GameSave.Serialize(session.Player.Execution));
    }

    [TestMethod]
    [DataRow("../outside")] [DataRow("..")] [DataRow("a/b")] [DataRow("a\\b")] [DataRow("C:evil")]
    [DataRow("UPPER")] [DataRow("")] [DataRow("con")] [DataRow("lpt1")] [DataRow("nul")] [DataRow("слот")]
    public async Task UnsafeIdsCannotReadWriteOrDelete(string id)
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project); var store = folder.Store(project);
        var snapshot = GameSlotStore.Capture(session);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(id, snapshot));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(id));
        Assert.IsEmpty(Directory.GetFileSystemEntries(folder.Path));
    }

    [TestMethod]
    public async Task TamperedMetadataStrictSchemaInvalidStateAndFileLimitAreDiagnosed()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project); var store = folder.Store(project);
        var snapshot = GameSlotStore.Capture(session); await store.SaveAsync("slot", snapshot);
        var path = System.IO.Path.Combine(folder.Slots, "slot.json"); var valid = File.ReadAllText(path);
        var json = JsonNode.Parse(valid)!; json["Metadata"]!["GameTime"] = 100;
        File.WriteAllText(path, json.ToJsonString()); Assert.AreEqual(GameSlotStatus.Corrupt, (await store.ReadAsync("slot")).Info.Status);
        json = JsonNode.Parse(valid)!; json["unexpected"] = true;
        File.WriteAllText(path, json.ToJsonString()); Assert.AreEqual(GameSlotStatus.Corrupt, (await store.ReadAsync("slot")).Info.Status);
        File.WriteAllText(path, valid.Replace("\"Version\": 2", "\"Version\": 2, \"Version\": 2", StringComparison.Ordinal));
        Assert.AreEqual(GameSlotStatus.Corrupt, (await store.ReadAsync("slot")).Info.Status);
        File.WriteAllText(path, valid.Replace("\"Title\":", "\"title\":", StringComparison.Ordinal));
        Assert.AreEqual(GameSlotStatus.Corrupt, (await store.ReadAsync("slot")).Info.Status);
        var state = JsonNode.Parse(snapshot.SaveJson)!; state["State"]!["NextActionId"] = Guid.NewGuid();
        json = JsonNode.Parse(valid)!; json["SaveJson"] = state.ToJsonString();
        File.WriteAllText(path, json.ToJsonString()); Assert.AreEqual(GameSlotStatus.Corrupt, (await store.ReadAsync("slot")).Info.Status);
        using (var file = File.Create(path)) file.SetLength(GameSlotStore.MaxFileBytes + 1);
        Assert.AreEqual(GameSlotStatus.Corrupt, (await store.ReadAsync("slot")).Info.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("slot", snapshot with { Metadata = snapshot.Metadata with { SceneId = Guid.NewGuid() } }));
        Assert.AreEqual(GameSlotStore.MaxFileBytes + 1L, new FileInfo(path).Length);
    }

    [TestMethod]
    public async Task CancelledOrFailedWritePreservesPreviousFileAndCleansTemporaryFiles()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project); var store = folder.Store(project);
        await store.SaveAsync("slot", GameSlotStore.Capture(session));
        var path = System.IO.Path.Combine(folder.Slots, "slot.json"); var before = File.ReadAllBytes(path);
        session.Player.Next(); var updated = GameSlotStore.Capture(session);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync("slot", updated, cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ReadAsync("slot", cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.ListAsync(cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.DeleteAsync("slot", cancelled.Token));
        if (OperatingSystem.IsWindows())
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var error = await Assert.ThrowsAsync<Exception>(() => store.SaveAsync("slot", updated));
            Assert.IsTrue(error is IOException or UnauthorizedAccessException);
        }
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.IsEmpty(Directory.GetFiles(folder.Slots, "*.tmp"));
        var inaccessible = new GameSlotStore(project, System.IO.Path.Combine(path, "slots"), folder.Quick);
        await Assert.ThrowsAsync<IOException>(() => inaccessible.SaveAsync("slot", updated));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task ConcurrentOperationsPublishWholeSnapshotsAndCapacityDoesNotOverwriteExistingSlots()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project); var store = folder.Store(project);
        var first = GameSlotStore.Capture(session); session.Player.Next(); var second = GameSlotStore.Capture(session);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveAsync("same", i % 2 == 0 ? first : second)));
        var read = await store.ReadAsync("same"); Assert.IsTrue(read.Snapshot == first || read.Snapshot == second);
        Assert.IsEmpty(Directory.GetFiles(folder.Slots, "*.tmp"));
        for (var i = 0; i < GameSlotStore.MaxSlots - 1; i++) File.WriteAllText(System.IO.Path.Combine(folder.Slots, $"slot-{i}.json"), "broken");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("excess", first));
        await store.SaveAsync("same", first); Assert.AreEqual(first, (await store.ReadAsync("same")).Snapshot);
        File.WriteAllText(System.IO.Path.Combine(folder.Slots, "excess.json"), "broken");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync());
    }

    [TestMethod]
    public async Task LinkedSlotIsRejectedWithoutTouchingTarget()
    {
        using var folder = new Folder(); var project = Project(); using var session = Session(project); var store = folder.Store(project);
        Directory.CreateDirectory(folder.Slots); var outside = System.IO.Path.Combine(folder.Path, "outside.json"); File.WriteAllText(outside, "untouched");
        var link = System.IO.Path.Combine(folder.Slots, "link.json");
        try { File.CreateSymbolicLink(link, outside); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { Assert.Inconclusive("Symlink requires host privileges."); }
        try
        {
            Assert.AreEqual(GameSlotStatus.Unavailable, (await store.ReadAsync("link")).Info.Status);
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync("link", GameSlotStore.Capture(session)));
            await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync("link"));
            Assert.AreEqual("untouched", File.ReadAllText(outside));
        }
        finally { File.Delete(link); }
    }

    private sealed class DelayedStore : IGameSlotStore
    {
        public TaskCompletionSource<GameSlotRead> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public GameSlotSnapshot? Captured;
        public Task SaveAsync(string id, GameSlotSnapshot snapshot, CancellationToken cancellationToken = default)
        { Captured = snapshot; Token = cancellationToken; return Reply.Task; }
        public Task<GameSlotRead> ReadAsync(string id, CancellationToken cancellationToken = default)
        { Token = cancellationToken; return Reply.Task; }
        public Task<GameSlotRead?> LatestAsync(CancellationToken cancellationToken = default) => throw new IOException("disk unavailable");
        public Task<ImmutableArray<GameSlotInfo>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(ImmutableArray<GameSlotInfo>.Empty);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task ResettingCannotStartSlotOperationsAndThrowingCancellationStillObservesLateFailure()
    {
        var project = Project(); project = project with { StartScreenSceneId = project.Scenes[0].Id };
        using var session = Session(project); session.NewGame(0); var store = new DelayedStore();
        await using var slots = new GameSessionSlots(session, store); Assert.IsTrue(slots.Load());
        using var registration = store.Token.Register(() => throw new IOException("cancel callback failed"));
        bool[]? accepted = null;
        session.Resetting += () => accepted = [slots.Save(), slots.Load(), slots.Continue(), slots.List(), slots.Delete("slot")];
        Assert.IsTrue(session.ReturnToMenu(session.Generation));
        Assert.IsNotNull(accepted); Assert.IsTrue(accepted.All(result => !result));
        Assert.HasCount(1, session.CleanupDiagnostics); Assert.IsFalse(slots.IsBusy);
        store.Reply.SetException(new IOException("late ignored failure")); await Task.Yield();
        Assert.IsFalse(slots.Poll()); Assert.IsNull(slots.LastResult);
    }

    [TestMethod]
    [DataRow(GameSlotStatus.Corrupt)] [DataRow(GameSlotStatus.ForeignProject)]
    [DataRow(GameSlotStatus.IncompatibleRevision)] [DataRow(GameSlotStatus.Compatible)]
    public async Task InvalidLoadKeepsPendingLuaAndDoesNotTriggerHostCleanup(GameSlotStatus status)
    {
        var project = Project(); var scene = project.Scenes[0];
        project = project with { Scenes = [scene with { ScriptBlocks = [new(null, ScriptPhase.SceneStart,
            new ScriptAction(Guid.NewGuid(), "game.set('x','1')", "pending"))] }] };
        var tcs = new TaskCompletionSource<ImmutableArray<ScenarioAction>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken luaToken = default;
        using var session = new GameSession(project, scene.Id, "ru", null, PlaybackMode.Game, (_, _, _, token) => { luaToken = token; return tcs.Task; });
        var old = session.Player; var state = old.Execution.State; var resets = 0; session.Resetting += () => resets++;
        var store = new DelayedStore(); await using var slots = new GameSessionSlots(session, store);
        Assert.IsTrue(slots.Load());
        var invalid = status == GameSlotStatus.Compatible ? new GameSlotSnapshot(null!, "broken") : null;
        store.Reply.SetResult(new(new("slot", status, null, "invalid slot"), invalid));
        await Finish(slots); Assert.IsFalse(slots.LastResult!.Success); Assert.AreSame(old, session.Player);
        Assert.AreSame(state, old.Execution.State); Assert.AreEqual(0L, session.Generation); Assert.AreEqual(0, resets);
        Assert.IsTrue(old.Execution.HasPendingLua); Assert.IsFalse(luaToken.IsCancellationRequested); tcs.SetResult([]);
    }

    [TestMethod]
    public async Task DisposingPendingSlotsCancelsAndRejectsLaterRequests()
    {
        var project = Project(); using var session = Session(project); var store = new DelayedStore();
        var slots = new GameSessionSlots(session, store); Assert.IsTrue(slots.Load());
        slots.Dispose(); Assert.IsTrue(store.Token.IsCancellationRequested); Assert.IsFalse(slots.IsBusy);
        store.Reply.SetException(new IOException("late disposal failure")); await slots.DisposeAsync();
        Assert.IsFalse(slots.Load()); Assert.IsFalse(slots.Save()); Assert.IsFalse(slots.Poll());
        Assert.AreEqual(0L, session.Generation);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task LateLoadAfterMenuNewGameIsCancelledAndCannotReplaceOrReportIntoNewGeneration(bool failure)
    {
        var project = Project(); project = project with { StartScreenSceneId = project.Scenes[0].Id };
        using var session = Session(project); session.NewGame(0); var snapshot = GameSlotStore.Capture(session);
        var store = new DelayedStore(); await using var slots = new GameSessionSlots(session, store);
        Assert.IsTrue(slots.Load()); Assert.IsFalse(slots.Load()); var generation = session.Generation;
        session.ReturnToMenu(generation); session.NewGame(session.Generation); var current = session.Player;
        session.Player.Next(); var state = session.Player.Execution.State;
        Assert.IsTrue(store.Token.IsCancellationRequested); Assert.IsFalse(slots.IsBusy);
        if (failure) store.Reply.SetException(new IOException("late failure"));
        else store.Reply.SetResult(new(new("quicksave", GameSlotStatus.Compatible, snapshot.Metadata, null), snapshot));
        await Task.Yield(); Assert.IsFalse(slots.Poll()); Assert.AreSame(current, session.Player);
        Assert.AreSame(state, session.Player.Execution.State); Assert.IsNull(slots.LastResult);
    }

    [TestMethod]
    public async Task CompletedLoadWaitsForHostPollAndRestoresWithoutTimeOrCommands()
    {
        var project = Project(); using var session = Session(project); session.Player.Tick(TimeSpan.FromSeconds(2));
        var snapshot = GameSlotStore.Capture(session); session.Player.Next();
        var store = new DelayedStore(); await using var slots = new GameSessionSlots(session, store);
        Assert.IsTrue(slots.Load()); var old = session.Player;
        store.Reply.SetResult(new(new("quicksave", GameSlotStatus.Compatible, snapshot.Metadata, null), snapshot));
        await Task.Delay(10); Assert.AreSame(old, session.Player); await Finish(slots);
        Assert.IsTrue(slots.LastResult!.Success); Assert.AreEqual(snapshot.SaveJson, GameSave.Serialize(session.Player.Execution));
        Assert.IsFalse(session.Player.Execution.HasPendingLua); Assert.AreNotSame(old, session.Player);
    }

    [TestMethod]
    public async Task FailedLoadAndUnavailableContinueKeepLiveStateAndAllowNextOperation()
    {
        var project = Project(); using var session = Session(project); var state = session.Player.Execution.State;
        var store = new DelayedStore(); await using var slots = new GameSessionSlots(session, store);
        Assert.IsTrue(slots.Load()); store.Reply.SetException(new UnauthorizedAccessException("denied"));
        await Finish(slots); Assert.IsFalse(slots.LastResult!.Success); Assert.AreSame(state, session.Player.Execution.State);
        Assert.IsTrue(slots.Continue()); await Finish(slots); Assert.IsFalse(slots.LastResult!.Success); Assert.AreEqual(0L, session.Generation);
        Assert.IsTrue(slots.List()); await Finish(slots); Assert.IsTrue(slots.LastResult!.Success); Assert.IsEmpty(slots.LastResult.Slots);
        Assert.IsTrue(slots.Delete("slot")); await Finish(slots); Assert.IsTrue(slots.LastResult!.Success);
    }

    [TestMethod]
    public async Task SaveCapturesBeforeIoAndMenuOrPendingLuaCannotOverwriteExistingSlot()
    {
        var project = Project(); using var session = Session(project); var store = new DelayedStore();
        await using var slots = new GameSessionSlots(session, store); var before = GameSave.Serialize(session.Player.Execution);
        Assert.IsTrue(slots.Save()); session.Player.Next(); Assert.AreEqual(before, store.Captured!.SaveJson);
        store.Reply.SetResult(new(new("quicksave", GameSlotStatus.Compatible, store.Captured.Metadata, null), store.Captured));
        await Finish(slots); Assert.IsTrue(slots.LastResult!.Success);
        var scene = project.Scenes[0]; var pendingProject = project with { Scenes = [scene with { ScriptBlocks = [
            new(null, ScriptPhase.SceneStart, new ScriptAction(Guid.NewGuid(), "game.set('x','1')", "pending"))] }] };
        var tcs = new TaskCompletionSource<ImmutableArray<ScenarioAction>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pending = new GameSession(pendingProject, scene.Id, "ru", null, PlaybackMode.Game, (_, _, _, _) => tcs.Task);
        using var pendingSlots = new GameSessionSlots(pending, store); var captured = store.Captured;
        Assert.IsFalse(pendingSlots.Save()); Assert.AreSame(captured, store.Captured);
        var menuProject = project with { StartScreenSceneId = scene.Id };
        using var menu = Session(menuProject); using var menuSlots = new GameSessionSlots(menu, store);
        Assert.IsFalse(menuSlots.Save()); Assert.AreSame(captured, store.Captured);
        tcs.SetResult([]);
    }
}
