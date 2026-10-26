using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class MinigameTests
{
    internal static LuaValue Data => LuaValue.FromJson("{\"score\":1}");
    internal static MinigameDefinition Definition => new()
    {
        ParametersSchema = new() { Fields = ImmutableDictionary<string, LuaValueKind>.Empty.Add("score", LuaValueKind.Number) },
        DefaultParameters = Data, InitialState = Data,
        StateSchema = new() { Fields = ImmutableDictionary<string, LuaValueKind>.Empty.Add("score", LuaValueKind.Number) },
        ResultSchema = new() { Fields = ImmutableDictionary<string, LuaValueKind>.Empty.Add("score", LuaValueKind.Number) },
    };
    internal static NovelProject Project()
    {
        var resource = NovelScene.Create("Мини-игра");
        var script = new LuaScriptAsset(Guid.NewGuid(), "minigame.lua", "return {on_event=function() end}");
        resource = resource with { Canvas = resource.Canvas with { HandlerScriptId = script.Id, Minigame = Definition } };
        var scene = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Продолжение"), DialogueLine.Create("ru", "Победа")] };
        scene = scene with { ScriptBlocks = [new(null, ScriptPhase.SceneStart, new StartMinigameAction(Guid.NewGuid(), resource.Id, Data)),
            new(null, ScriptPhase.SceneStart, new BranchMinigameAction(Guid.NewGuid(), "score", Data.Properties!["score"], scene.Id, ScenarioProgram.LineEntryId(scene.Lines[1].Id)))] };
        return NovelProject.Create("Проверка мини-игры") with { Scenes = [scene, resource], Scripts = [script] };
    }

    [TestMethod]
    public void DirectScenarioWorkerProjectsResultAndChecksLaunchPermissionAndParameterSchema()
    {
        var project=Project();using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");
        var catalog=LuaCommandCatalog.Capture(project,project.Scenes[0].Id);var state=LuaStateSnapshot.Capture(player.State);
        var script=new ScriptAction(Guid.NewGuid(),"game.start_minigame(refs.puzzle,{score=2})","launch"){References=ImmutableDictionary<string,Guid>.Empty.Add("puzzle",project.Scenes[1].Id)};
        var commands=LuaActionScript.RunInWorker(script,state,catalog);Assert.AreEqual(2d,((StartMinigameAction)commands[0]).Parameters.Properties!["score"].Number);
        foreach(var source in new[]{"game.start_minigame('"+Guid.NewGuid()+"')","game.start_minigame(refs.puzzle,{score=false})"})
            Assert.Throws<InvalidOperationException>(()=>LuaActionScript.RunInWorker(script with {Source=source},state,catalog));
        var noGame=catalog with {Minigames=null};Assert.IsFalse(noGame.IsValid(commands[0]));
        var read=new ScriptAction(Guid.NewGuid(),"local r=game.minigame_result(); if r==nil then game.set('empty','true') end","empty");
        Assert.AreEqual("true",((SetAction)LuaActionScript.RunInWorker(read,state,catalog)[0]).Value);
        var resultState=state with {MinigameOutcome=new(project.Scenes[1].Id,new(MinigamePhase.Completed,Data,""))};
        var result=LuaActionScript.RunInWorker(read with {Source="local r=game.minigame_result();game.set('result',r.data.score);game.set('status',r.status)"},resultState,catalog);
        Assert.AreEqual("1",((SetAction)result[0]).Value);Assert.AreEqual("completed",((SetAction)result[1]).Value);
    }

    [TestMethod]
    [DataRow("$status",true)][DataRow("missing",false)]
    public void ScenarioBranchesOnStatusOrFallsThroughMissingResultField(string field,bool expected)
    {
        var project=Project();var scene=project.Scenes[0];var branch=(BranchMinigameAction)scene.ScriptBlocks[1].Action;
        project=project with {Scenes=project.Scenes.SetItem(0,scene with {ScriptBlocks=scene.ScriptBlocks.SetItem(1,scene.ScriptBlocks[1] with {Action=branch with {Field=field,EqualsValue=LuaValue.FromString("cancelled")}})})};
        using var player=new ScenarioPlayer(project,scene.Id,"ru");player.FinishMinigame(player.State.Minigame!.RunId,new(MinigamePhase.Cancelled,LuaValue.Null,""));
        Assert.AreEqual(scene.Lines[expected?1:0].Id,player.State.LineId);
        var (copy,ids)=SceneCopy.Create(project.Scenes[0]);Assert.AreEqual(ids[scene.Id],((BranchMinigameAction)copy.ScriptBlocks[1].Action).SceneId);
        Assert.AreEqual(project.Scenes[1].Id,((StartMinigameAction)copy.ScriptBlocks[0].Action).SceneId);
    }

    [TestMethod]
    public void InvalidResourceActionAndOutcomeAreRejectedBeforePersistenceOrRestore()
    {
        var project=Project();var scene=project.Scenes[0];var launch=(StartMinigameAction)scene.ScriptBlocks[0].Action;
        foreach(var action in new[]{launch with {SceneId=Guid.NewGuid()},launch with {Parameters=LuaValue.Null}})
            Assert.IsFalse(ScenarioProgram.IsValidAction(project,scene,action));
        var invalidResource=project.Scenes[1] with {Canvas=project.Scenes[1].Canvas with {HandlerScriptId=null}};
        Assert.Throws<ArgumentException>(()=>NovellMaker.Core.Screens.ScreenAsset.ValidateProject(project with {Scenes=project.Scenes.SetItem(1,invalidResource)}));
        using var player=new ScenarioPlayer(project,scene.Id,"ru");player.FinishMinigame(player.State.Minigame!.RunId,new(MinigamePhase.Completed,Data,""));
        Assert.Throws<ArgumentException>(()=>GameSave.Validate(player.State with {MinigameOutcome=new(Guid.NewGuid(),new(MinigamePhase.Completed,Data,""))},project));
        Assert.Throws<ArgumentException>(()=>GameSave.Validate(player.State with {MinigameOutcome=new(project.Scenes[1].Id,null!)},project));
        var json=JsonNode.Parse(GameSave.Serialize(player))!;json["Version"]=1;Assert.Throws<ArgumentException>(()=>GameSave.ReadState(json.ToJsonString()));
    }

    [TestMethod]
    public void QueuedMinigameDataUsesTheExistingTotalCharacterBudget()
    {
        var id=Guid.NewGuid();var data=LuaValue.FromString(new('x',8192));
        Assert.IsTrue(LuaLimits.ValidQueue([new StartMinigameAction(Guid.NewGuid(),id,data),new BranchMinigameAction(Guid.NewGuid(),"field",data,id,Guid.NewGuid())]));
        Assert.IsFalse(LuaLimits.ValidQueue(Enumerable.Range(0,65).Select(_=>new StartMinigameAction(Guid.NewGuid(),id,data))));
        Assert.IsFalse(LuaLimits.ValidQueue([new StartMinigameAction(Guid.NewGuid(),id,null!)]));
        Assert.IsFalse(LuaLimits.ValidQueue([new BranchMinigameAction(Guid.NewGuid(),"field",null!,id,Guid.NewGuid())]));
    }

    [TestMethod]
    [DataRow(MinigamePhase.Completed)] [DataRow(MinigamePhase.Cancelled)] [DataRow(MinigamePhase.Failed)]
    public void LifecycleTransitionsCleanExactlyOnceAndPreserveOutcomeAfterDisposal(MinigamePhase finish)
    {
        var calls = 0; var snapshot = new MinigameSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Data, Data, LuaRandomState.Initial);
        var run = new MinigameRun(Definition, snapshot, () => calls++);
        Assert.AreEqual(MinigamePhase.Starting, run.Phase); run.Pause(true); Assert.AreEqual(MinigamePhase.Starting, run.Phase);
        run.Commit(Data, LuaRandomState.FromSeed(12)); Assert.IsTrue(run.Snapshot.Initialized); run.Start();
        Assert.Throws<InvalidOperationException>(run.Start); run.Pause(true); Assert.AreEqual(MinigamePhase.Paused, run.Phase);
        Assert.Throws<InvalidOperationException>(() => run.Commit(Data, LuaRandomState.Initial)); run.Pause(false);
        var result = new MinigameResult(finish, finish == MinigamePhase.Completed ? Data : LuaValue.Null, "Результат");
        Assert.IsTrue(run.Finish(result)); Assert.AreEqual(finish, run.Phase); Assert.AreEqual(1, calls);
        Assert.IsFalse(run.Finish(result)); run.Pause(true); run.Dispose(); run.Dispose();
        Assert.AreEqual(1, calls); Assert.AreEqual(result, run.Result); Assert.AreEqual(MinigamePhase.Disposed, run.Phase);
        Assert.Throws<InvalidOperationException>(() => run.Commit(Data, LuaRandomState.Initial));
    }

    [TestMethod]
    public void DisposeFromStartingAndPausedCancelsOnceAndRejectsInvalidResultAtomically()
    {
        foreach (var paused in new[] { false, true })
        {
            var calls = 0; using var run = new MinigameRun(Definition, new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Data, Data, LuaRandomState.Initial), () => calls++);
            if (paused) { run.Start(); run.Pause(true); }
            Assert.Throws<ArgumentException>(() => run.Finish(new(MinigamePhase.Completed, LuaValue.Null, "")));
            Assert.AreEqual(0, calls); Assert.IsTrue(run.IsActive); run.Dispose();
            Assert.AreEqual(MinigamePhase.Cancelled, run.Result!.Phase); Assert.AreEqual(1, calls);
        }
    }

    [TestMethod]
    [DataRow("{}")] [DataRow("{\"score\":false}")] [DataRow("{\"score\":1,\"extra\":2}")]
    [DataRow("[]")] [DataRow("null")]
    public void SchemaRejectsMissingExtraAndWrongTypes(string json) => Assert.Throws<ArgumentException>(() => Definition.StateSchema.Validate(LuaValue.FromJson(json)));

    [TestMethod]
    public void ResourceAndSnapshotRejectInvalidDataAndBudgets()
    {
        var invalid = new[] { Definition with { UpdatesPerSecond = 0 }, Definition with { UpdatesPerSecond = 21 }, Definition with { UpdatesPerSecond = double.NaN },
            Definition with { SaveDisabledReason = "" }, Definition with { SaveDisabledReason = new('x',257) }, Definition with { ParametersSchema = null! },
            Definition with { StateSchema = null! }, Definition with { ResultSchema = null! }, Definition with { InitialState = LuaValue.Null } };
        foreach (var value in invalid) Assert.Throws<ArgumentException>(value.Validate);
        foreach (var schema in new[] { new MinigameSchema { Kind = (LuaValueKind)99 }, new MinigameSchema { Fields = null! },
            new MinigameSchema { Kind = LuaValueKind.String, Fields = Definition.StateSchema.Fields },
            new MinigameSchema { Fields = ImmutableDictionary<string,LuaValueKind>.Empty.Add("",LuaValueKind.String) },
            new MinigameSchema { Fields = ImmutableDictionary<string,LuaValueKind>.Empty.Add("bad",(LuaValueKind)99) },
            new MinigameSchema { Fields = Enumerable.Range(0,65).ToImmutableDictionary(i=>"key"+i,_=>LuaValueKind.String) } })
            Assert.Throws<ArgumentException>(schema.ValidateDefinition);
        var snapshot = new MinigameSnapshot(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Data, Data, LuaRandomState.Initial);
        foreach (var value in new[] { snapshot with { RunId = Guid.Empty }, snapshot with { ActionId = Guid.Empty }, snapshot with { SceneId = Guid.Empty },
            snapshot with { RandomState = null! }, snapshot with { Parameters = LuaValue.Null }, snapshot with { State = LuaValue.Null } })
            Assert.Throws<ArgumentException>(() => value.Validate(Definition));
        foreach (var phase in new[] { MinigamePhase.Starting, MinigamePhase.Running, MinigamePhase.Paused, MinigamePhase.Disposed })
            Assert.Throws<ArgumentException>(() => new MinigameResult(phase,Data,"").Validate(Definition));
        Assert.Throws<ArgumentException>(() => new MinigameResult(MinigamePhase.Cancelled,Data,"").Validate(Definition));
        Assert.Throws<ArgumentException>(() => new MinigameResult(MinigamePhase.Failed,LuaValue.Null,new('x',1025)).Validate(Definition));
    }

    [TestMethod]
    [DataRow(true)] [DataRow(false)]
    public void ScenarioBarrierRestoresDataAndRngAndBranchesOnce(bool won)
    {
        var project = Project(); using var player = new ScenarioPlayer(project,project.Scenes[0].Id,"ru");
        var active = player.State.Minigame!; Assert.AreEqual("minigame",player.State.Wait!.Mode);
        Assert.IsFalse(player.Advance()); player.Tick(TimeSpan.FromSeconds(1)); Assert.AreEqual(0d,player.State.Time);
        Assert.Throws<InvalidOperationException>(()=>GameSave.Capture(player));
        var confirmed = active with { Initialized = true, RandomState = LuaRandomState.FromSeed(42) };
        Assert.IsFalse(player.CommitMinigame(confirmed with { RunId = Guid.NewGuid() }));
        Assert.IsFalse(player.CommitMinigame(confirmed with { ActionId = Guid.NewGuid() }));
        Assert.IsFalse(player.CommitMinigame(confirmed with { SceneId = Guid.NewGuid() }));
        Assert.IsFalse(player.CommitMinigame(confirmed with { Parameters = LuaValue.Null }));
        Assert.Throws<ArgumentException>(()=>player.CommitMinigame(confirmed with { State = LuaValue.Null }));
        Assert.IsTrue(player.CommitMinigame(confirmed));
        player.MinigameSaveBarrier=()=>"Занято"; Assert.Throws<InvalidOperationException>(()=>GameSave.Capture(player)); player.MinigameSaveBarrier=null;
        var saved=GameSave.Serialize(player); using var restored=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");
        GameSave.Load(restored,saved); Assert.IsTrue(restored.MinigameRestored); Assert.AreEqual(confirmed.RandomState,restored.State.Minigame!.RandomState);
        Assert.AreEqual(confirmed.State.ToJson(),restored.State.Minigame.State.ToJson()); Assert.AreEqual(active.RunId,restored.State.Minigame.RunId);
        var result = new MinigameResult(won?MinigamePhase.Completed:MinigamePhase.Cancelled,won?Data:LuaValue.Null,"");
        Assert.IsFalse(restored.FinishMinigame(Guid.NewGuid(),result)); Assert.IsTrue(restored.FinishMinigame(active.RunId,result));
        Assert.IsFalse(restored.FinishMinigame(active.RunId,result));
        Assert.AreEqual(project.Scenes[0].Lines[won?1:0].Id,restored.State.LineId);
        GameSave.Validate(restored.State,project); Assert.AreEqual(result,restored.State.MinigameOutcome!.Result);
    }

    [TestMethod]
    public void InvalidLoadPreservesActiveRunAndSavePolicyHasReason()
    {
        var project=Project(); using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");
        player.CommitMinigame(player.State.Minigame! with { Initialized=true }); var before=player.State;
        foreach(var state in new[] {before with {Minigame=null}, before with {Wait=null},before with {Minigame=before.Minigame! with {State=LuaValue.Null}},
            before with {Minigame=before.Minigame! with {SceneId=Guid.NewGuid()}}, before with {MinigameOutcome=new(project.Scenes[1].Id,new(MinigamePhase.Completed,Data,""))},
            before with {Wait=before.Wait! with {ActionId=Guid.NewGuid()}}, before with {Minigame=before.Minigame! with {ActionId=project.Scenes[0].ScriptBlocks[1].Action.Id}},
            before with {Minigame=before.Minigame! with {Initialized=false}},before with {Error="bad"} })
        {Assert.Throws<ArgumentException>(()=>player.Restore(state));Assert.AreSame(before,player.State);}
        var resource=project.Scenes[1]; project=project with {Scenes=project.Scenes.SetItem(1,resource with {Canvas=resource.Canvas with {Minigame=Definition with {CanSave=false,SaveDisabledReason="Только после пазла"}}})};
        using var locked=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");locked.CommitMinigame(locked.State.Minigame! with {Initialized=true});
        StringAssert.Contains(Assert.Throws<InvalidOperationException>(()=>GameSave.Capture(locked)).Message,"Только после пазла");
    }

    [TestMethod]
    public async Task Version19RoundTripOldVersionsRejectNewResourceAndMigrationDoesNotWrite()
    {
        var project=Project();var path=Path.Combine(TestPaths.TempDirectory,Guid.NewGuid()+".novell");
        try
        {
            await new JsonProjectStore().SaveAsync(path,project);var text=File.ReadAllText(path);
            Assert.IsTrue(project.ContentEquals(await new JsonProjectStore().LoadAsync(path)));
            var json=JsonNode.Parse(text)!;json["formatVersion"]=18;File.WriteAllText(path,json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(()=>new JsonProjectStore().LoadAsync(path));
            json["scenes"]![1]!["canvas"]!.AsObject().Remove("minigame");
            json["scenes"]![0]!["scriptBlocks"]=new JsonArray(); var old=json.ToJsonString();File.WriteAllText(path,old);
            var migrated=await new JsonProjectStore().LoadAsync(path);Assert.AreEqual(19,migrated.FormatVersion);Assert.AreEqual(old,File.ReadAllText(path));
            Assert.IsTrue(migrated.Scenes.All(s=>s.Canvas.Minigame is null));
            using var player=new ScenarioPlayer(migrated,migrated.Scenes[0].Id,"ru");var save=JsonNode.Parse(GameSave.Serialize(player))!;save["Version"]=1;
            GameSave.Load(player,save.ToJsonString());
        }
        finally {File.Delete(path);}
    }

    [TestMethod]
    public async Task ScenarioLuaLaunchUsesDefaultsAndReadsStructuredOutcomeWithoutChangingLegacyFinish()
    {
        var project=Project();using var player=new ScenarioPlayer(project,project.Scenes[0].Id,"ru");
        var script=new ScriptAction(Guid.NewGuid(),"game.start_minigame(refs.puzzle); game.finish('legacy')","launch") {References=ImmutableDictionary<string,Guid>.Empty.Add("puzzle",project.Scenes[1].Id)};
        var commands=await LuaActionScript.RunAsync(script,player.State,project);
        Assert.IsInstanceOfType<StartMinigameAction>(commands[0]);Assert.AreEqual(Data.ToJson(),((StartMinigameAction)commands[0]).Parameters.ToJson());
        Assert.AreEqual("minigame.result",((SetAction)commands[1]).Variable);
        player.FinishMinigame(player.State.Minigame!.RunId,new(MinigamePhase.Completed,Data,""));
        var read=await LuaActionScript.RunAsync(new(Guid.NewGuid(),"local r=game.minigame_result(); game.set('status',r.status); game.set('score',r.data.score)","read"),player.State,project);
        Assert.AreEqual("completed",((SetAction)read[0]).Value);Assert.AreEqual("1",((SetAction)read[1]).Value);
    }

    [TestMethod]
    public async Task QueuedLuaLaunchCanSaveAndRestoreBeforeReadingItsResultInTheNextBlock()
    {
        var project=Project();var story=project.Scenes[0];var target=project.Scenes[1].Id;
        story=story with {ScriptBlocks=[new(null,ScriptPhase.SceneStart,new ScriptAction(Guid.NewGuid(),"game.start_minigame('"+target+"',{score=1})","launch")),
            new(null,ScriptPhase.SceneStart,new ScriptAction(Guid.NewGuid(),"game.set('score',game.minigame_result().data.score)","result"))]};
        project=project with {Scenes=project.Scenes.SetItem(0,story)};using var player=new ScenarioPlayer(project,story.Id,"ru");
        async Task Drain(ScenarioPlayer value)
        {while(value.HasPendingLua){await value.PendingLuaTask!;value.PollLua();}}
        await Drain(player);Assert.IsNotNull(player.State.Minigame);var active=player.State.Minigame!;
        player.CommitMinigame(active with {Initialized=true});var saved=GameSave.Serialize(player);
        using var restored=new ScenarioPlayer(project,story.Id,"ru",LuaActionScript.RequestCommandsAsync,startImmediately:false);
        GameSave.Load(restored,saved);restored.IsPaused=false;Assert.IsFalse(restored.HasPendingLua);
        restored.FinishMinigame(active.RunId,new(MinigamePhase.Completed,Data,""));await Drain(restored);
        Assert.AreEqual("1",restored.State.Variables["score"]);Assert.AreEqual(story.Lines[0].Id,restored.State.LineId);
    }
}
