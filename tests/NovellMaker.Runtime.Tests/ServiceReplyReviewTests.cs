using System.Collections.Concurrent;
using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Editor.Examples;

namespace NovellMaker.Runtime.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ServiceReplyReviewTests
{
    private sealed class Store(bool automatic = false) : IPlayerProfileStore
    {
        internal TaskCompletionSource Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<PlayerProfileData> Writes = [];
        internal PlayerProfileData? Committed;
        public Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default) => Task.FromResult(
            Committed is { } data ? new PlayerProfileRead(data.Settings, PlayerProfileStatus.Loaded, "") { Data = data.Data } : new(defaults, PlayerProfileStatus.Missing, ""));
        public async Task SaveAsync(PlayerProfileData data, CancellationToken token = default)
        {
            var completion = Gate; Writes.Enqueue(data);
            if (!automatic) await completion.Task;
            Committed = data;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal readonly Store Store;
        internal readonly GameSession Game;
        internal readonly PlayerSettingsSession Profile;
        internal readonly RuntimeSettingsServices Bridge;
        internal readonly RuntimeScreens Host;
        internal readonly List<GameServiceResult> Results = [];
        internal int Requests;
        internal ScreenInstance Origin => Host.Stack.Instances.Last();
        internal Rig(NovelProject project, Guid sceneId, bool automatic = false)
        {
            Store = new(automatic); Game = new(project, sceneId, "ru");
            Profile = new(Store, new(PlayerSettings.Defaults(project.Audio), PlayerProfileStatus.Missing, ""));
            Bridge = new(Game, Profile, _ => { });
            Host = new(Guid.NewGuid(), enableLua: true, hostService: (origin, command, generation) =>
            { Requests++; Bridge.Request(origin, command, generation); });
            Bridge.IsCurrentOrigin = origin => Host.Stack.Instances.Any(s => ReferenceEquals(origin, s));
            Bridge.Completed += (origin, result) => { Results.Add(result); Host.DeliverServiceResult(origin, result); };
            Host.Configure(project, () => Game.Player.Execution.State.Variables, () => Game.Generation); Host.SyncScene(sceneId);
        }
        internal static Rig Example()
        {
            var story = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("ru", "line")] };
            var rig = new Rig(ExampleTriggersModule.AddTo(NovelProject.Create("Review") with { Scenes = [story] }), story.Id);
            Assert.IsTrue(rig.Host.RouteKey("F8")); return rig;
        }
        internal void Click(int x) { Host.Route(new(ScreenInputKind.PointerDown, x, 490)); Host.Route(new(ScreenInputKind.PointerUp, x, 490)); }
        internal async Task Until(Func<bool> done)
        {
            for (var i = 0; i < 3000; i++)
            {
                Host.Poll(); Bridge.Poll();
                if (done()) return;
                await Task.Delay(2);
            }
            Assert.Fail("Runtime service/worker did not settle.");
        }
        internal Task Drain() => Until(() => !Host.HasPending && !Profile.IsBusy);
        internal bool HasText(string value) => Origin.Definition.Nodes.Any(n => n.Text == value);
        public async ValueTask DisposeAsync()
        { Store.Gate.TrySetResult(); await Profile.FlushAsync(); await Host.DisposeAsync(); Game.Dispose(); }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task RealWorkerAndProfileBridgeStopReadOrDiskWriteCyclesAcrossServiceResults(bool write)
    {
        var source = """
            local count=0
            return {on_load=function() ui.trigger('again') end,on_event=function(e)
              if e.kind=='trigger' then count=count+1; COMMAND
              elseif e.kind=='service_result' then ui.trigger('again') end
            end}
            """.Replace("COMMAND", write ? "ui.set_persistent('count',''..count)" : "ui.get_persistent('count')", StringComparison.Ordinal);
        var script = new LuaScriptAsset(Guid.NewGuid(), "cycle.lua", source);
        var scene = NovelScene.Create("Cycle"); scene = scene with { Canvas = scene.Canvas with { HandlerScriptId = script.Id } };
        await using var rig = new Rig(NovelProject.Create("Cycle") with { Scenes = [scene], Scripts = [script] }, scene.Id, automatic: true);
        var origin = rig.Origin; await rig.Drain();
        Assert.IsFalse(rig.Host.Scripts!.IsAvailable(origin)); Assert.AreEqual(16, rig.Requests);
        Assert.AreEqual(write ? 16 : 0, rig.Store.Writes.Count);
        Assert.IsTrue(rig.Host.Diagnostics.Any(s => s.Contains("глубины", StringComparison.Ordinal)));
        Assert.AreEqual(0, rig.Host.Scripts.ReservedServiceReplies);
    }

    [TestMethod]
    public async Task CompletedWriteReplyCannotBeLostBehindAFullInputQueueAndUnblocksActualExample()
    {
        await using var rig = Rig.Example(); await rig.Drain(); var origin = rig.Origin;
        rig.Click(350); await rig.Until(() => rig.Store.Writes.Count == 1);
        Assert.AreEqual(1, rig.Host.Scripts!.ReservedServiceReplies);
        for (var i = 0; i < ScreenLuaSession.MaximumQueuedEvents - 1; i++)
            Assert.AreEqual(ScreenEventAdmission.Accepted, rig.Host.Scripts.EnqueueUsingInstanceSnapshot(origin, new("update")));
        Assert.AreEqual(ScreenEventAdmission.Busy, rig.Host.Scripts.EnqueueUsingInstanceSnapshot(origin, new("update")));
        rig.Store.Gate.SetResult();
        // Keep input queued while the completed profile service attempts delivery.
        for (var i = 0; i < 3000 && !rig.Results.Any(r => r.Action == GameServiceAction.SetPersistent); i++)
        { rig.Bridge.Poll(); await Task.Delay(2); }
        Assert.IsTrue(rig.Results.Any(r => r.Action == GameServiceAction.SetPersistent));
        Assert.AreEqual(0, rig.Host.Scripts.ReservedServiceReplies); Assert.AreEqual(128, rig.Host.Scripts.QueuedEvents);
        Assert.IsTrue(rig.Results.Last().Success); Assert.AreEqual(true, rig.Results.Last().ProfileSaved);
        await rig.Drain();
        Assert.IsTrue(rig.Host.Scripts.IsAvailable(origin)); Assert.IsTrue(rig.HasText("Отмечено: Первый шаг"));
        Assert.IsTrue(origin.Definition.Buttons.All(b => b.Enabled));
        Assert.AreEqual("true", rig.Store.Committed!.Data["first_steps"]);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ReadOrReopenAfterFailedWriteCannotTurnVolatileMarkIntoAConfirmedOne(bool reopen)
    {
        await using var rig = Rig.Example(); await rig.Drain();
        rig.Click(350); await rig.Until(() => rig.Store.Writes.Count == 1);
        rig.Store.Gate.SetException(new IOException("test disk failure")); await rig.Drain();
        Assert.IsTrue(rig.HasText("Не записано: Первый шаг")); Assert.IsNull(rig.Store.Committed);
        if (reopen) { rig.Host.Route(new(ScreenInputKind.Back)); rig.Host.Poll(); rig.Host.RouteKey("F8"); }
        else rig.Click(900);
        await rig.Drain();
        var read = rig.Results.Last(); Assert.AreEqual(GameServiceAction.GetPersistent, read.Action);
        Assert.IsTrue(read.Success); Assert.AreEqual(false, read.ProfileSaved); Assert.AreEqual("true", read.DataValue);
        Assert.IsTrue(rig.HasText("Не записано: Первый шаг")); Assert.AreEqual(1, rig.Store.Writes.Count);
        rig.Store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Click(350); await rig.Until(() => rig.Store.Writes.Count == 2);
        rig.Store.Gate.SetResult(); await rig.Drain();
        Assert.IsTrue(rig.HasText("Отмечено: Первый шаг")); Assert.AreEqual(true, rig.Results.Last().ProfileSaved);
        rig.Click(350); await rig.Drain(); Assert.AreEqual(2, rig.Store.Writes.Count);
        var restarted = new PlayerSettingsSession(rig.Store, await rig.Store.ReadAsync(PlayerSettings.Defaults(new())));
        Assert.AreEqual("true", restarted.GetPersistent("first_steps")); Assert.IsTrue(restarted.IsPersisted);
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ProjectButtonReservesAReplyBeforeExecutingItsService(bool full)
    {
        var script = new LuaScriptAsset(Guid.NewGuid(), "binding.lua", "return {on_event=function(e) if e.kind=='service_result' then ui.set_text(refs.label,'answered') end end}");
        var asset = ScreenFixture.Create("Binding"); var label = asset.Nodes[1].Id;
        asset = asset with { HandlerScriptId = script.Id, References = ImmutableDictionary<string, Guid>.Empty.Add("label", label),
            Buttons = [new(asset.Nodes[2].Id, ScreenButtonAction.ListSlots, null)] };
        GameServiceCommand? request = null;
        await using var host = new RuntimeScreens(Guid.NewGuid(), enableLua: true, hostService: (_, command, _) => request = command);
        host.Configure(NovelProject.Create("Binding") with { Scenes = [asset.ToScene()], Scripts = [script] }, () => ImmutableDictionary<string, string>.Empty);
        host.SyncScene(asset.Id); await DrainHost(host); var origin = host.Stack.Instances.Single();
        if (full) for (var i = 0; i < 128; i++) host.Scripts!.EnqueueUsingInstanceSnapshot(origin, new("update"));
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate));
        if (full)
        { Assert.IsNull(request); Assert.AreEqual(0, host.Scripts!.ReservedServiceReplies); Assert.IsTrue(host.Diagnostics.Any(s => s.Contains("места", StringComparison.Ordinal))); }
        else
        {
            Assert.IsNotNull(request); Assert.AreEqual(1, host.Scripts!.ReservedServiceReplies);
            Assert.IsTrue(host.DeliverServiceResult(origin, new(request.RequestId, request.Action, true, "", null, [])));
            await DrainHost(host); Assert.IsTrue(origin.Definition.Nodes.Any(n => n.Text == "answered"));
        }
    }
    private static async Task DrainHost(RuntimeScreens host)
    {
        for (var i = 0; i < 3000; i++) { host.Poll(); if (!host.HasPending) return; await Task.Delay(2); }
        Assert.Fail("Host did not settle.");
    }
}
