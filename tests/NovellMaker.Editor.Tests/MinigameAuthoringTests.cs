using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Scripting;
using NovellMaker.Editor.Examples;
using NovellMaker.Editor.Services;

namespace NovellMaker.Editor.Tests;

public sealed partial class ExternalScriptTests
{
    [TestMethod]
    public async Task MinigameResourceHistorySaveAsAndStableReferencesRoundTrip()
    {
        using var root=new TestRoot();var dialogs=new Dialogs(root.Path);using var session=await Session(dialogs,editor:new CodeEditor());
        var before=session.Project!;session.AddMinigameExample();var added=session.Project!;
        Assert.AreEqual(before.Scenes.Length+3,added.Scenes.Length);Assert.IsNotNull(added.Scenes.Last().Canvas.Minigame);
        session.Undo();Assert.IsTrue(before.ContentEquals(session.Project));session.Redo();Assert.IsTrue(added.ContentEquals(session.Project));
        await session.SaveAsync();dialogs.SavePath=Path.Combine(root.Path,"copy","puzzle.novell");await session.SaveAsync(true);
        var copy=await new JsonProjectStore().LoadAsync(dialogs.SavePath);Assert.IsTrue(added.ContentEquals(copy));
        Assert.AreEqual(ExampleMinigameModule.Source,await File.ReadAllTextAsync(ScriptFiles.Resolve(Path.GetDirectoryName(dialogs.SavePath)!,copy.Scripts.Last().Id)));
        session.OpenScreenLibrary(copy.Scenes.Last().Id);session.DuplicateScreen();var duplicate=session.Project!.Scenes.Last();
        Assert.AreNotEqual(copy.Scenes.Last().Id,duplicate.Id);Assert.IsNotNull(duplicate.Canvas.Minigame);
        Assert.IsTrue(duplicate.Canvas.References.Values.All(id=>duplicate.Canvas.Nodes.Any(n=>n.Id==id)));
        Assert.IsEmpty(dialogs.Errors);
    }

    [TestMethod]
    public async Task ActualPuzzleRestoresVisibleSelectionLayoutAndRngThenCompletesFourPairs()
    {
        var project=ExampleMinigameModule.AddTo(NovelProject.Create("Пазл"));var story=project.Scenes[0];var puzzle=project.Scenes.Last();
        using var player=new ScenarioPlayer(project,story.Id,"ru");var original=player.State.Minigame!;
        var stack=new ScreenStack(Guid.NewGuid());await using var scripts=new ScreenLuaSession(stack);
        var first=stack.Open(Guid.NewGuid(),puzzle.AsScreen().Definition());
        void Attach(ScreenInstance instance,LuaValue state,LuaRandomState random,bool restoring)=>scripts.Attach(instance,project.Scripts.Last().Source,
            puzzle.Canvas.References,state:state,modules:new(project.Scripts.Last().Id,project.Scripts),randomState:random,
            minigame:puzzle.Canvas.Minigame,invocation:new(original.Parameters,restoring));
        async Task Drain()
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(scripts.HasPending) {timeout.Token.ThrowIfCancellationRequested();scripts.Poll();if(scripts.PendingTask is {} task)await task;else await Task.Delay(1);}
            Assert.IsEmpty(scripts.Diagnostics);
        }
        async Task Click(ScreenInstance instance,int card)
        {scripts.Enqueue(instance,new("click",puzzle.Canvas.References["card"+card]),player.State.Variables);await Drain();}
        Attach(first,original.State,original.RandomState,false);await Drain();await Click(first,1);
        var data=scripts.GetState(first);var rng=scripts.GetRandomState(first);
        player.CommitMinigame(original with {State=data,RandomState=rng,Initialized=true});var saved=GameSave.Serialize(player);
        stack.Close(first);await Drain();GameSave.Load(player,saved);
        var second=stack.Open(Guid.NewGuid(),puzzle.AsScreen().Definition());Attach(second,player.State.Minigame!.State,player.State.Minigame.RandomState,true);await Drain();
        Assert.AreEqual(data.ToJson(),scripts.GetState(second).ToJson());Assert.AreEqual(rng,scripts.GetRandomState(second));
        Assert.AreEqual(first.Definition.Nodes.Single(n=>n.Id==puzzle.Canvas.References["card1"]).Text,
            second.Definition.Nodes.Single(n=>n.Id==puzzle.Canvas.References["card1"]).Text);
        var deck=data.Properties!["deck"].Items;var mate=Enumerable.Range(2,7).Single(i=>deck[i-1].Number==deck[0].Number);await Click(second,mate);
        foreach(var group in Enumerable.Range(2,7).Where(i=>i!=mate).GroupBy(i=>deck[i-1].Number))foreach(var i in group)await Click(second,i);
        var result=scripts.GetMinigameResult(second)!;Assert.IsNotNull(result);Assert.AreEqual(4d,result.Data.Properties!["moves"].Number);
        Assert.AreEqual(96d,result.Data.Properties["score"].Number);Assert.IsTrue(player.FinishMinigame(original.RunId,result));
        Assert.AreEqual(project.Scenes[1].Id,player.State.SceneId);Assert.IsFalse(player.FinishMinigame(original.RunId,result));
    }
}
