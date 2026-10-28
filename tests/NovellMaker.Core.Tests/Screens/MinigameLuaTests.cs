using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Tests.Execution;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class MinigameLuaTests
{
    private static readonly ImmutableDictionary<string,string> Empty=ImmutableDictionary<string,string>.Empty;
    private static ScreenChange Complete => new("minigame_result",null,null,null) {MinigameResult=new(MinigamePhase.Completed,MinigameTests.Data,"")};

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public void WorkerVmProjectsParametersRestoringAndCompletionWithoutExposingClr(bool restoring)
    {
        var vm=new ScreenLuaVm(ScreenLuaProtocolTests.Definition(),Guid.NewGuid(),"""
            return {on_load=function()
              local s=ui.minigame_parameters()
              if ui.is_restoring() then s.score=s.score+1 end
              ui.set_state(s);ui.complete_minigame(s)
            end,on_event=function() ui.cancel_minigame() end}
            """,ImmutableDictionary<string,Guid>.Empty,minigame:new(MinigameTests.Data,restoring));
        var batch=ScreenLuaProtocol.Prepare(vm.Definition,vm.Handle(null,Empty,MinigameTests.Data));
        Assert.AreEqual(restoring?2d:1d,batch.MinigameResult!.Data.Properties!["score"].Number);
        var cancelled=ScreenLuaProtocol.Prepare(vm.Definition,vm.Handle(new("update"),Empty));
        Assert.AreEqual(MinigamePhase.Cancelled,cancelled.MinigameResult!.Phase);
        Assert.AreEqual(LuaValueKind.Null,cancelled.MinigameResult.Data.Kind);
    }

    [TestMethod]
    public async Task RealWorkerReceivesParametersAndCommitsStructuredCompletion()
    {
        var (stack,instance)=ScreenLuaSessionTests.Fixture();await using var session=new ScreenLuaSession(stack);
        session.Attach(instance,"return {on_event=function() ui.set_state(ui.minigame_parameters()); ui.complete_minigame(ui.get_state()) end}",
            state:MinigameTests.Data,minigame:MinigameTests.Definition,invocation:new(MinigameTests.Data,false));
        await ScreenLuaSessionTests.Drain(session);Assert.IsTrue(session.IsSettled(instance));
        session.Enqueue(instance,new("update",Seconds:0.2),Empty);await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(MinigamePhase.Completed,session.GetMinigameResult(instance)!.Phase);
        Assert.AreEqual(MinigameTests.Data.ToJson(),session.GetState(instance).ToJson());Assert.IsEmpty(session.Diagnostics);
    }

    [TestMethod]
    [DataRow("ui.set_state({score=2})")]
    [DataRow("ui.seed_random(99)")]
    [DataRow("ui.complete_minigame({score=1})")]
    [DataRow("ui.close()")]
    [DataRow("ui.main_menu()")]
    [DataRow("ui.trigger('again')")]
    [DataRow("ui.animate(ui.tween(refs.target,{x=50},1))")]
    [DataRow("ui.play_audio(audio.sample,'effect',true,0.5)")]
    public async Task RestoreCannotChangeStateRngOrProduceEffects(string code)
    {
        var (stack,instance)=ScreenLuaSessionTests.Fixture();var before=instance.Definition;await using var session=new ScreenLuaSession(stack);
        session.Attach(instance,"return {on_load=function() "+code+" end,on_event=function() end}",ImmutableDictionary<string,Guid>.Empty.Add("target",before.Nodes[1].Id),state:MinigameTests.Data,
            minigame:MinigameTests.Definition,invocation:new(MinigameTests.Data,true),audioReferences:ImmutableDictionary<string,Guid>.Empty.Add("sample",Guid.NewGuid()));
        await ScreenLuaSessionTests.Drain(session);Assert.IsFalse(session.IsAvailable(instance));Assert.AreSame(before,instance.Definition);
        Assert.AreEqual(MinigameTests.Data.ToJson(),session.GetState(instance).ToJson());Assert.AreEqual(LuaRandomState.Initial,session.GetRandomState(instance));
    }

    [TestMethod]
    [DataRow("ui.complete_minigame({score=false})")]
    [DataRow("ui.set_state({score=false})")]
    [DataRow("ui.complete_minigame({score=1}); ui.complete_minigame({score=1})")]
    [DataRow("ui.complete_minigame({score=1}); ui.close()")]
    [DataRow("ui.main_menu(); ui.complete_minigame({score=1})")]
    public async Task InvalidBatchDoesNotCommitStateRandomVisualsOrCompletion(string body)
    {
        var (stack,instance)=ScreenLuaSessionTests.Fixture();var before=instance.Definition;await using var session=new ScreenLuaSession(stack);
        session.Attach(instance,"return {on_event=function() ui.set_text(refs.label,'bad'); ui.random(); "+body+" end}",
            ImmutableDictionary<string,Guid>.Empty.Add("label",before.Nodes[2].Id),state:MinigameTests.Data,
            minigame:MinigameTests.Definition,invocation:new(MinigameTests.Data,false));
        await ScreenLuaSessionTests.Drain(session);session.Enqueue(instance,new("update"),Empty);await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(before,instance.Definition);Assert.IsNull(session.GetMinigameResult(instance));
        Assert.AreEqual(MinigameTests.Data.ToJson(),session.GetState(instance).ToJson());Assert.AreEqual(LuaRandomState.Initial,session.GetRandomState(instance));
        Assert.IsEmpty(session.DrainServices());Assert.IsFalse(session.IsAvailable(instance));
    }

    [TestMethod]
    [DataRow("ui.complete_minigame({})")][DataRow("ui.cancel_minigame()")][DataRow("ui.minigame_parameters()")][DataRow("ui.is_restoring()")]
    public void OrdinaryScreenCannotUseMinigameApi(string code)
    {
        var vm=new ScreenLuaVm(ScreenLuaProtocolTests.Definition(),Guid.NewGuid(),"return {on_load=function() "+code+" end,on_event=function() end}",ImmutableDictionary<string,Guid>.Empty);
        Assert.Throws<InterpreterException>(()=>vm.Handle(null,Empty));
    }

    [TestMethod]
    public async Task PausedCompletionDoesNotBlockMenuAndCommitsOnlyAfterResume()
    {
        var (stack,instance)=ScreenLuaSessionTests.Fixture();var connection=new ScreenLuaSessionTests.Connection();
        await using var session=new ScreenLuaSession(stack,()=>connection);
        session.Attach(instance,"return {}",state:MinigameTests.Data,minigame:MinigameTests.Definition,invocation:new(MinigameTests.Data,false));
        session.Poll();(await connection.Next()).Reply();await ScreenLuaSessionTests.Drain(session);
        session.Enqueue(instance,new("update"),Empty);session.Poll();var completion=await connection.Next();
        session.Suspend(instance,true);completion.Reply(Complete);await session.PendingTask!;
        session.Poll();Assert.IsNull(session.GetMinigameResult(instance));Assert.IsFalse(session.IsSettled(instance));
        var menu=stack.Open(Guid.NewGuid(),ScreenLuaProtocolTests.Definition());session.Attach(menu,"return {}");session.Poll();
        (await connection.Next()).Reply();await session.PendingTask!;session.Poll();Assert.IsTrue(session.IsSettled(menu));
        Assert.IsFalse(session.HasRunnablePending);session.Suspend(instance,false);session.Poll();
        Assert.AreEqual(MinigamePhase.Completed,session.GetMinigameResult(instance)!.Phase);
        Assert.IsFalse(session.HasPending);
    }

    [TestMethod]
    [DataRow(false)][DataRow(true)]
    public async Task ClosingOrResettingPausedInstanceDropsLateCompletionAndReleasesConversation(bool reset)
    {
        var (stack,instance)=ScreenLuaSessionTests.Fixture();var connection=new ScreenLuaSessionTests.Connection();
        await using var session=new ScreenLuaSession(stack,()=>connection);
        session.Attach(instance,"return {}",state:MinigameTests.Data,minigame:MinigameTests.Definition,invocation:new(MinigameTests.Data,false));
        session.Poll();var load=await connection.Next();session.Suspend(instance,true);load.Reply(Complete);
        await session.PendingTask!;session.Poll();Assert.AreEqual(0,session.AppliedBatches);
        if(reset)session.Reset();else stack.Close(instance);
        await ScreenLuaSessionTests.Drain(session);Assert.AreEqual(0,session.AppliedBatches);Assert.IsNull(session.GetMinigameResult(instance));
    }

    [TestMethod]
    public async Task BrokenWorkerConversationInvalidatesMiniGameWithoutPublishingAPartialResult()
    {
        var (stack,instance)=ScreenLuaSessionTests.Fixture();var connection=new ScreenLuaSessionTests.Connection();await using var session=new ScreenLuaSession(stack,()=>connection);
        session.Attach(instance,"return {}",state:MinigameTests.Data,minigame:MinigameTests.Definition,invocation:new(MinigameTests.Data,false));
        session.Poll();(await connection.Next()).Reply();await ScreenLuaSessionTests.Drain(session);
        session.Enqueue(instance,new("update"),Empty);session.Poll();var call=await connection.Next();call.Completion.SetException(new IOException("Worker exited"));
        await ScreenLuaSessionTests.Drain(session);Assert.IsFalse(session.IsAvailable(instance));Assert.IsNull(session.GetMinigameResult(instance));
        Assert.AreEqual(MinigameTests.Data.ToJson(),session.GetState(instance).ToJson());Assert.AreEqual(LuaRandomState.Initial,session.GetRandomState(instance));
        Assert.AreEqual(ScreenLuaIssueKind.Conversation,session.Issues.Last().Kind);
    }

    [TestMethod]
    public void CompletionWireRejectsMixedAndDuplicatePayloads()
    {
        var definition=ScreenLuaProtocolTests.Definition();var valid=Complete;
        foreach(var bad in new[]{valid with {Kind="state"},valid with {NodeId=Guid.NewGuid()},valid with {Text="x"},valid with {Value=true},
            valid with {Patch=new()},valid with {State=LuaValue.Null},valid with {RandomState=LuaRandomState.Initial},valid with {Trigger=new("trigger")}})
            Assert.Throws<InvalidOperationException>(()=>ScreenLuaProtocol.Prepare(definition,[bad]));
        Assert.Throws<InvalidOperationException>(()=>ScreenLuaProtocol.Prepare(definition,[valid,valid]));
        Assert.Throws<InvalidOperationException>(()=>ScreenLuaProtocol.Prepare(definition,[new("close",null,null,null),valid]));
    }
}
