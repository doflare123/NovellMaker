using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Runtime.Tests;

[TestClass]
public sealed class RuntimeMinigameTests
{
    private sealed class ReleaseProvider : NovellMaker.Core.ExternalServices.IReleaseCheckProvider
    {
        internal int Calls;
        internal readonly TaskCompletionSource<NovellMaker.Core.ExternalServices.ReleaseProviderReply> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NovellMaker.Core.ExternalServices.ReleaseProviderReply> CheckAsync(NovellMaker.Core.ExternalServices.ReleaseCheckConfiguration configuration, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Result.Task; }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task ReleaseResultWaitsForMinigameLifecyclePauseAndIsDeliveredOnce(bool pauseScenario)
    {
        var project = Project("""
            return {on_load=function() ui.check_game_release() end,on_event=function(e)
              if e.kind=='service_result' then
                local state=ui.get_state(); state.deliveries=(state.deliveries or 0)+1
                ui.set_state(state); ui.set_text(refs.label,'DELIVERED')
              end
            end}
            """);
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player);
        var provider = new ReleaseProvider();
        await using var service = new NovellMaker.Core.ExternalServices.ReleaseCheckService(project.Id, true, provider, new("author", "novel", "1.0.0"));
        host.Scripts!.ConnectReleaseChecks(service); await Drain(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (provider.Calls == 0) { timeout.Token.ThrowIfCancellationRequested(); host.Poll(); await Task.Delay(1); }
        var instance = host.Stack.Instances.Single(); var before = instance.Definition;
        var overlay = host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Слой").Definition(), new(true, pauseScenario));
        host.Poll(); Assert.AreEqual(MinigamePhase.Paused, host.ActiveMinigame!.Phase);
        provider.Result.SetResult(new(NovellMaker.Core.ExternalServices.ReleaseCheckCode.NetworkUnavailable));
        while (service.HasRunningTransport) { timeout.Token.ThrowIfCancellationRequested(); host.Poll(); await Task.Delay(1); }
        await Drain(host);
        Assert.AreEqual(MinigamePhase.Paused, host.ActiveMinigame.Phase);
        Assert.AreSame(before, instance.Definition);
        Assert.IsFalse(host.Scripts.GetState(instance).Properties!.ContainsKey("deliveries"));
        Assert.AreEqual(1, service.ReservationCount); Assert.AreEqual(1, host.Scripts.ReservedServiceReplies);
        host.Stack.Close(overlay); await Drain(host);
        Assert.AreEqual(MinigamePhase.Running, host.ActiveMinigame.Phase);
        Assert.AreEqual("DELIVERED", instance.Definition.Nodes[1].Text);
        Assert.AreEqual(1d, host.Scripts.GetState(instance).Properties!["deliveries"].Number);
        Assert.AreEqual(0, service.ReservationCount); Assert.AreEqual(0, host.Scripts.ReservedServiceReplies);
        await Drain(host);
        Assert.AreEqual(1d, host.Scripts.GetState(instance).Properties!["deliveries"].Number);
        Assert.IsEmpty(host.Diagnostics);
    }

    [TestMethod]
    public async Task ReleaseRequestBlocksSaveUntilHandledAndLoadDoesNotReplayNetwork()
    {
        var project = Project("return {on_load=function() if not ui.is_restoring() then ui.check_game_release() end end,on_event=function(e) if e.kind=='service_result' then ui.set_text(refs.label,e.code) end end}");
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player);
        var provider = new ReleaseProvider();
        await using var service = new NovellMaker.Core.ExternalServices.ReleaseCheckService(project.Id, true, provider, new("author", "novel", "1.0.0"));
        host.Scripts!.ConnectReleaseChecks(service); await Drain(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (provider.Calls == 0) { timeout.Token.ThrowIfCancellationRequested(); host.Poll(); await Task.Delay(1); }
        var instance = host.Stack.Instances.Single();
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player)).Message, "проверки выпуска");
        Assert.Throws<ArgumentException>(() => GameSave.Load(player, "{}")); host.Poll();
        Assert.IsTrue(host.Scripts.HasReleaseChecks(instance));
        provider.Result.SetResult(new(NovellMaker.Core.ExternalServices.ReleaseCheckCode.NetworkUnavailable));
        while (host.Scripts.HasReleaseChecks(instance)) { timeout.Token.ThrowIfCancellationRequested(); host.Poll(); await Task.Delay(1); }
        await Drain(host); Assert.AreEqual("NetworkUnavailable", instance.Definition.Nodes[1].Text);
        var saved = GameSave.Serialize(player);
        Assert.IsFalse(saved.Contains("CheckGameRelease", StringComparison.Ordinal));
        GameSave.Load(player, saved); host.Reset(); host.SyncScene(player.State.SceneId); host.Poll(); await Drain(host);
        Assert.AreEqual(1, provider.Calls); Assert.AreEqual(0, service.ReservationCount);
        Assert.IsNotNull(GameSave.Capture(player)); Assert.IsEmpty(host.Diagnostics);
    }

    [TestMethod]
    [DataRow("ui.check_game_release()")]
    [DataRow("ui.cancel_release_check('11111111111111111111111111111111')")]
    public async Task RestoringRejectsReleaseCommandsBeforeEffects(string command)
    {
        var project = Project("return {on_load=function() if ui.is_restoring() then ui.set_text(refs.label,'bad'); " + command + " end end,on_event=function() end}");
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player);
        var provider = new ReleaseProvider();
        await using var service = new NovellMaker.Core.ExternalServices.ReleaseCheckService(project.Id, true, provider, new("author", "novel", "1.0.0"));
        host.Scripts!.ConnectReleaseChecks(service); await Drain(host);
        var saved = GameSave.Serialize(player); GameSave.Load(player, saved); host.Reset(); host.SyncScene(player.State.SceneId); host.Poll(); await Drain(host);
        Assert.AreEqual(0, provider.Calls); Assert.AreEqual(0, service.ReservationCount);
        Assert.IsNull(host.ActiveMinigame);
        Assert.AreEqual(MinigamePhase.Failed, player.State.MinigameOutcome!.Result.Phase);
        StringAssert.Contains(player.State.MinigameOutcome.Result.Message, "Восстановление");
    }

    private sealed class AudioBackend : NovellMaker.Core.Audio.IAudioBackend
    {
        internal int Opened, Disposed;
        internal bool Finished;
        public NovellMaker.Core.Audio.IAudioSource Open(string path, bool loop) { Opened++; return new Source(this); }
        private sealed class Source(AudioBackend backend) : NovellMaker.Core.Audio.IAudioSource
        {
            public float Volume { set { } }
            public bool Finished => backend.Finished;
            public void Update() { }
            public void Pause(bool paused) { }
            public void Dispose() => backend.Disposed++;
        }
    }

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public async Task OwnedAudioAndItsPendingResultBlockSaveAndLoadNeverReplaysIt(bool finishNaturally)
    {
        var project = Project("""
            local source=nil
            return {on_load=function() if not ui.is_restoring() then source=ui.play_audio(audio.sample,'effect',false,0.25) end end,
              on_event=function(e)
                if e.kind=='click' then ui.stop_audio(source)
                elseif e.kind=='audio_result' then ui.set_text(refs.label,e.status) end
              end}
            """);
        var id = Guid.NewGuid(); project = project with { Audio = new() { Assets = [new(id, "sample.wav", $"Assets/Audio/{id:N}.wav")] },
            Scripts = [project.Scripts[0] with { References = ImmutableDictionary<string, Guid>.Empty.Add("sample", id) }] };
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player);
        var backend = new AudioBackend(); using var audio = new NovellMaker.Core.Audio.AudioService(project, Path.GetTempPath(), "ru", backend);
        host.Scripts!.ConnectAudio(audio); await Drain(host); Assert.AreEqual(1, audio.ActiveSources);
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player)).Message, "звук");
        Assert.Throws<ArgumentException>(() => GameSave.Load(player, "{}")); host.Poll(); Assert.AreEqual(1, audio.ActiveSources);
        if (finishNaturally) { backend.Finished = true; host.Scripts.PollAudio(_ => true); }
        else
        {
            var instance = host.Stack.Instances.Single(); host.Scripts.EnqueueUsingInstanceSnapshot(instance, new("click", instance.Definition.Nodes[2].Id));
            await Drain(host); Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player)); host.Scripts.PollAudio(_ => true);
        }
        Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player)); await Drain(host);
        var saved = GameSave.Serialize(player); Assert.AreEqual(0, audio.ActiveSources);
        GameSave.Load(player, saved); host.Poll(); await Drain(host); Assert.AreEqual(1, backend.Opened); Assert.AreEqual(1, backend.Disposed);
        Assert.IsNotNull(GameSave.Capture(player)); Assert.IsEmpty(host.Diagnostics);
    }

    [TestMethod]
    [DataRow("ui.complete_minigame({})")] [DataRow("ui.cancel_minigame()")]
    public async Task CompletingOrCancellingMiniGameDisposesItsOwnSound(string completion)
    {
        var project = Project("return {on_load=function() ui.play_audio(audio.sample,'sound',true,1) end,on_event=function(e) if e.kind=='click' then " + completion + " end end}");
        var id = Guid.NewGuid(); project = project with { Audio = new() { Assets = [new(id, "sample.wav", $"Assets/Audio/{id:N}.wav")] },
            Scripts = [project.Scripts[0] with { References = ImmutableDictionary<string, Guid>.Empty.Add("sample", id) }] };
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player);
        var backend = new AudioBackend(); using var audio = new NovellMaker.Core.Audio.AudioService(project, Path.GetTempPath(), "ru", backend);
        host.Scripts!.ConnectAudio(audio); await Drain(host); Assert.AreEqual(1, audio.ActiveSources);
        host.Route(new(ScreenInputKind.FocusNext)); host.Route(new(ScreenInputKind.Activate)); await Drain(host);
        Assert.AreEqual(0, audio.ActiveSources); Assert.AreEqual(1, backend.Disposed); Assert.IsNull(host.ActiveMinigame); Assert.IsEmpty(host.Diagnostics);
    }
    [TestMethod]
    [DataRow(.2, 5d)] [DataRow(.5, 5d)] [DataRow(.05, 20d)] [DataRow(1d, 20d)]
    public async Task FrameAdvancesAnimationsBeforeFrequentMinigameUpdatesAndUnblocksSave(double frameSeconds, double updatesPerSecond)
    {
        var project = Project("""
            return {on_load=function() ui.animate(ui.tween(refs.label,{x=100},1)) end,
              on_event=function(e)
                local s=ui.get_state()
                if e.kind=='update' then s.updates=(s.updates or 0)+1
                elseif e.kind=='animation_result' then
                  s.completions=(s.completions or 0)+1;s.status=e.status
                end
                ui.set_state(s)
              end}
            """);
        var game = project.Scenes[1];
        project = project with { Scenes = project.Scenes.SetItem(1, game with { Canvas = game.Canvas with
            { Minigame = game.Canvas.Minigame! with { UpdatesPerSecond = updatesPerSecond } } }) };
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player); await Drain(host);
        var instance = host.Stack.Instances.Single(); Assert.AreEqual(40, instance.Definition.Nodes[1].Bounds.X);
        Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player));
        for (var i = 0; i < Math.Ceiling(2 / frameSeconds); i++)
        { host.Tick(TimeSpan.FromSeconds(frameSeconds), true); await Drain(host); }
        Assert.AreEqual(100, instance.Definition.Nodes[1].Bounds.X);
        Assert.IsFalse(host.Scripts!.HasAnimations(instance));
        var state = host.Scripts.GetState(instance).Properties!;
        Assert.AreEqual(1, state["completions"].Number); Assert.AreEqual("completed", state["status"].String);
        Assert.IsTrue(state["updates"].Number > 0); Assert.IsNotNull(GameSave.Capture(player)); Assert.IsEmpty(host.Diagnostics);
    }

    [TestMethod]
    public async Task FrameSynchronizesFocusAndMenuPauseBeforeAdvancingAnimations()
    {
        var project = Project("return {on_load=function() ui.animate(ui.tween(refs.label,{x=100},1)) end,on_event=function() end}");
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player); await Drain(host);
        var instance = host.Stack.Instances.Single();
        host.Tick(TimeSpan.FromSeconds(.2), false); await Drain(host); Assert.AreEqual(40, instance.Definition.Nodes[1].Bounds.X);
        host.Tick(TimeSpan.FromSeconds(.2), true); await Drain(host); Assert.AreEqual(52, instance.Definition.Nodes[1].Bounds.X, 1e-8);
        var menu = host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Меню").Definition());
        host.Tick(TimeSpan.FromSeconds(2), true); await Drain(host); Assert.AreEqual(52, instance.Definition.Nodes[1].Bounds.X, 1e-8);
        host.Stack.Close(menu); host.Tick(TimeSpan.FromSeconds(.2), true); await Drain(host); Assert.AreEqual(64, instance.Definition.Nodes[1].Bounds.X, 1e-8);
        host.Tick(TimeSpan.FromSeconds(1), true); await Drain(host); Assert.IsNotNull(GameSave.Capture(player));
    }
    [TestMethod]
    public async Task AnimationsBlockSavePauseUnderMenusAndRestoreWithoutReplay()
    {
        var project = Project("return {on_load=function() if not ui.is_restoring() then ui.animate(ui.tween(refs.label,{x=100},1)) end end,on_event=function() end}");
        using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru"); await using var host = Host(project, () => player); await Drain(host);
        Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player));
        var instance = host.Stack.Instances.Single();
        host.TickAnimations(TimeSpan.FromSeconds(.5), true); Assert.AreEqual(70, instance.Definition.Nodes[1].Bounds.X);
        host.TickAnimations(TimeSpan.FromSeconds(10), false); Assert.AreEqual(70, instance.Definition.Nodes[1].Bounds.X);
        var menu = host.Stack.Open(Guid.NewGuid(), ScreenFixture.Create("Меню").Definition());
        host.TickAnimations(TimeSpan.FromSeconds(10), true); Assert.AreEqual(70, instance.Definition.Nodes[1].Bounds.X);
        host.Stack.Close(menu); host.TickAnimations(TimeSpan.FromSeconds(.5), true);
        Assert.Throws<InvalidOperationException>(() => GameSave.Capture(player)); await Drain(host);
        Assert.IsFalse(host.Scripts!.HasAnimations(instance)); var saved = GameSave.Serialize(player);
        GameSave.Load(player, saved); host.Poll(); await Drain(host);
        Assert.AreEqual(0, host.Scripts.ActiveAnimations); Assert.IsEmpty(host.Diagnostics);
    }
    private static NovelProject Project(string? code=null)
    {
        var game=ScreenFixture.Create("Пазл").ToScene();
        var script=new LuaScriptAsset(Guid.NewGuid(),"puzzle.lua",code??"return {on_load=function() ui.set_text(refs.label,'ready') end,on_event=function(e) if e.kind=='click' then ui.set_state({steps=2}); ui.complete_minigame({won=true}) end end}");
        game=game with {Canvas=game.Canvas with {Buttons=[],HandlerScriptId=script.Id,References=ImmutableDictionary<string,Guid>.Empty.Add("label",game.Canvas.Nodes[1].Id),
            Minigame=new(){InitialState=LuaValue.FromJson("{\"steps\":1}")}}};
        var story=NovelScene.Create("Сюжет") with {Lines=[DialogueLine.Create("ru","После пазла")]};
        story=story with {ScriptBlocks=[new(null,ScriptPhase.SceneStart,new StartMinigameAction(Guid.NewGuid(),game.Id,LuaValue.FromObject([])))]};
        return NovelProject.Create("Пазл") with {Scenes=[story,game],Scripts=[script]};
    }
    private static async Task Drain(RuntimeScreens screens)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        do {timeout.Token.ThrowIfCancellationRequested();screens.Poll();if(screens.Scripts?.PendingTask is {} task)try {await task;}catch(InvalidOperationException){}else await Task.Delay(1);}while(screens.HasPending);
        screens.Poll();
    }
    private static RuntimeScreens Host(NovelProject project,Func<ScenarioPlayer> player,bool lua=true)
    {
        var screens=new RuntimeScreens(Guid.NewGuid(),lua);screens.Configure(project,()=>player().State.Variables,execution:player);screens.SyncScene(player().State.SceneId);return screens;
    }

    [TestMethod]
    public async Task OwnedChildLayersAreCleanedWithMiniGameWhileIndependentMenuRemains()
    {
        var project=Project();var game=project.Scenes[1];var child=ScreenFixture.Create("Дочерний слой").ToScene();
        game=game with {Canvas=game.Canvas with {Buttons=[new(game.Canvas.Nodes[2].Id,ScreenButtonAction.Open,child.Id)]}};
        project=project with {Scenes=project.Scenes.SetItem(1,game).Add(child)};
        using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");await using var host=Host(project,()=>player);await Drain(host);
        var instance=host.Stack.Instances.Single();host.Route(new(ScreenInputKind.FocusNext));host.Route(new(ScreenInputKind.Activate));
        var ownedChild=host.Stack.Instances.Last();Assert.AreEqual(instance.OwnerId,ownedChild.OwnerId);
        var independent=host.Stack.Open(Guid.NewGuid(),ScreenFixture.Create("Независимое меню").Definition());
        host.Stack.Close(instance);host.Poll();await Drain(host);
        Assert.AreSame(independent,host.Stack.Instances.Single());Assert.IsNull(host.ActiveMinigame);Assert.IsTrue(player.IsPaused);
        Assert.AreEqual(MinigamePhase.Cancelled,player.State.MinigameOutcome!.Result.Phase);
        host.Stack.Close(independent);player.IsPaused=false;player.Tick(TimeSpan.FromSeconds(.01));Assert.AreEqual(project.Scenes[0].Lines[0].Id,player.State.LineId);
    }

    [TestMethod]
    public async Task CompletesOnceOwnsInstanceAndSaveBarrierPreservesCommittedSnapshot()
    {
        var project=Project();using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");await using var host=Host(project,()=>player);
        host.Poll();Assert.Throws<InvalidOperationException>(()=>GameSave.Capture(player));await Drain(host);
        var run=host.ActiveMinigame!;Assert.AreEqual(MinigamePhase.Running,run.Phase);Assert.AreEqual("{\"steps\":1}",player.State.Minigame!.State.ToJson());
        var saved=GameSave.Serialize(player);Assert.IsTrue(host.Stack.PausesScenario);Assert.HasCount(1,host.Stack.Instances);
        host.Route(new(ScreenInputKind.FocusNext));host.Route(new(ScreenInputKind.Activate));await Drain(host);
        Assert.IsNull(host.ActiveMinigame);Assert.AreEqual(MinigamePhase.Disposed,run.Phase);Assert.AreEqual(MinigamePhase.Completed,run.Result!.Phase);
        Assert.IsEmpty(host.Stack.Instances);Assert.IsNull(player.State.Minigame);Assert.AreEqual(project.Scenes[0].Lines[0].Id,player.State.LineId);
        Assert.IsFalse(player.FinishMinigame(run.Snapshot.RunId,run.Result));
        GameSave.Load(player,saved);host.Poll();await Drain(host);
        Assert.AreNotSame(run,host.ActiveMinigame);Assert.AreEqual("{\"steps\":1}",player.State.Minigame!.State.ToJson());
        host.Route(new(ScreenInputKind.Back));host.Poll();await Drain(host);
        Assert.AreEqual(MinigamePhase.Cancelled,player.State.MinigameOutcome!.Result.Phase);Assert.IsEmpty(host.Stack.Instances);
    }

    [TestMethod]
    public async Task UpdateRateCoalescesElapsedAndFocusOrOverlayPausesWithoutAccumulatingPausedTime()
    {
        var project=Project("return {on_event=function(e) if e.kind=='update' then local s=ui.get_state();s.steps=s.steps+e.seconds;ui.set_state(s) end end}");
        using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");await using var host=Host(project,()=>player);await Drain(host);
        host.TickMinigame(TimeSpan.FromSeconds(0.1),true);Assert.IsFalse(host.HasPending);
        host.TickMinigame(TimeSpan.FromSeconds(0.1),true);await Drain(host);Assert.AreEqual(1.2,player.State.Minigame!.State.Properties!["steps"].Number,0.00001);
        host.TickMinigame(TimeSpan.FromSeconds(5),false);Assert.AreEqual(MinigamePhase.Paused,host.ActiveMinigame!.Phase);
        host.TickMinigame(TimeSpan.Zero,true);Assert.AreEqual(MinigamePhase.Running,host.ActiveMinigame.Phase);
        var overlay=host.Stack.Open(Guid.NewGuid(),ScreenFixture.Create("Меню").Definition());
        host.TickMinigame(TimeSpan.FromSeconds(5),true);Assert.AreEqual(MinigamePhase.Paused,host.ActiveMinigame.Phase);
        host.Stack.Close(overlay);host.TickMinigame(TimeSpan.FromSeconds(10),true);await Drain(host);
        Assert.AreEqual(2.2,player.State.Minigame!.State.Properties!["steps"].Number,0.00001);
        Assert.Throws<ArgumentOutOfRangeException>(()=>host.TickMinigame(TimeSpan.FromSeconds(-1),true));
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task WorkerFailureOrMissingWorkerCleansOwnerAndContinuesWithFailedResult(bool enabled)
    {
        var project=Project("return {on_load=function() missing() end,on_event=function() end}");using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");
        await using var host=Host(project,()=>player,enabled);await Drain(host);
        Assert.IsNull(host.ActiveMinigame);Assert.IsEmpty(host.Stack.Instances);Assert.AreEqual(MinigamePhase.Failed,player.State.MinigameOutcome!.Result.Phase);
        Assert.AreEqual(project.Scenes[0].Lines[0].Id,player.State.LineId);Assert.IsNull(player.MinigameSaveBarrier);
    }

    [TestMethod]
    public async Task RepeatedResetAndPlayerReplacementDoNotKeepHandlersOwnersOrSaveBarriers()
    {
        var project=Project();using var session=new GameSession(project,project.Scenes[0].Id,"ru");await using var host=Host(project,()=>session.Player.Execution);
        for(var i=0;i<6;i++)
        {
            await Drain(host);var oldPlayer=session.Player.Execution;var oldRun=host.ActiveMinigame!;var saved=GameSave.Serialize(oldPlayer);
            session.LoadSave(saved);host.SyncScene(session.Player.Execution.State.SceneId,afterLoad:true);host.Poll();await Drain(host);
            Assert.IsNull(oldPlayer.MinigameSaveBarrier);Assert.AreEqual(MinigamePhase.Disposed,oldRun.Phase);Assert.HasCount(1,host.Stack.Instances);
            Assert.AreNotSame(oldRun,host.ActiveMinigame);
        }
        var run=host.ActiveMinigame!;host.Reset();host.Reset();Assert.AreEqual(MinigamePhase.Disposed,run.Phase);
        Assert.IsEmpty(host.Stack.Instances);Assert.IsNull(session.Player.Execution.MinigameSaveBarrier);
    }
}
