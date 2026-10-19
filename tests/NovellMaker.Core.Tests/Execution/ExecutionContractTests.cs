using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class ExecutionContractTests
{
    [TestMethod]
    public void GameContinuationFinishesLastLineAndDoesNotAllowAuthorSeeking()
    {
        var line = DialogueLine.Create("ru", "Последняя фраза");
        var scene = NovelScene.Create("Начало") with { Lines = [line] };
        var project = NovelProject.Create("Игра") with { Scenes = [scene] };
        var author = new ScenePlayer(project, scene.Id, "ru");
        Assert.IsFalse(author.Frame.CanNext);
        var game = new ScenePlayer(project, scene.Id, "ru", mode: PlaybackMode.Game);
        Assert.IsTrue(game.Frame.CanNext);
        Assert.IsFalse(game.Seek(line.Id)); Assert.IsFalse(game.Previous());
        Assert.IsTrue(game.Next()); Assert.IsTrue(game.Execution.State.Completed);
        Assert.IsFalse(game.Next());
        Assert.ThrowsExactly<ArgumentException>(() => new ScenePlayer(project, scene.Id, "ru", line.Id, PlaybackMode.Game));
    }

    [TestMethod]
    public void ScriptPreviewRetainsGameContinuationAfterJumpToLinearScene()
    {
        var line = DialogueLine.Create("ru", "Конечная сцена без блоков");
        var end = NovelScene.Create("Конец") with { Lines = [line] };
        var start = NovelScene.Create("Начало") with { ScriptBlocks = [new(null, ScriptPhase.SceneStart,
            new JumpAction(Guid.NewGuid(), end.Id, ScenarioProgram.LineEntryId(line.Id)))] };
        var project = NovelProject.Create("Переход") with { Scenes = [start, end] };
        var player = new ScenePlayer(project, start.Id, "ru");
        Assert.AreEqual(end.Id, player.Execution.State.SceneId);
        Assert.IsTrue(player.UsesActions);
        Assert.IsFalse(player.Seek(line.Id)); Assert.IsFalse(player.Previous());
        var save = GameSave.Serialize(player.Execution);
        Assert.IsTrue(player.Next()); Assert.IsTrue(player.Execution.State.Completed);
        player.LoadSave(save);
        Assert.AreEqual(line.Id, player.Frame.LineId); Assert.IsTrue(player.Frame.CanNext);
    }

    [TestMethod]
    public void DuringBlocksAreSequentialAndLuaComputesBeforeQueuedWaitWhileLoadDoesNotPump()
    {
        var line = DialogueLine.Create("ru", "Текст уже показан");
        var scene = NovelScene.Create("Сцена") with { Lines = [line], ScriptBlocks = [
            new(line.Id, ScriptPhase.DuringLine, new ScriptAction(Guid.NewGuid(),
                "game.wait(2)\nif game.time() == 0 then game.set('early', 'before') end\ngame.set('queued', 'once')", "first.lua")),
            new(line.Id, ScriptPhase.DuringLine, new ScriptAction(Guid.NewGuid(),
                "if game.time() == 2 then game.set('late', 'after') end", "second.lua")),
            new(line.Id, ScriptPhase.AfterLine, new SetAction(Guid.NewGuid(), "after", "yes"))
        ] };
        var project = NovelProject.Create("Игра") with { Scenes = [scene] };
        var game = new ScenePlayer(project, scene.Id, "ru", mode: PlaybackMode.Game);
        Assert.AreEqual(line.Id, game.Frame.LineId);
        Assert.AreEqual("timer", game.Execution.State.Wait?.Mode);
        Assert.IsFalse(game.Next()); Assert.IsFalse(game.Execution.State.Variables.ContainsKey("late"));
        game.Tick(TimeSpan.FromSeconds(1));
        var saved = GameSave.Serialize(game.Execution);
        game.Tick(TimeSpan.FromSeconds(1));
        Assert.AreEqual("before", game.Execution.State.Variables["early"]);
        Assert.AreEqual("after", game.Execution.State.Variables["late"]);
        game.LoadSave(saved);
        Assert.AreEqual(saved, GameSave.Serialize(game.Execution), "Load must only restore, without pumping commands or running Lua.");
        Assert.IsFalse(game.Execution.State.Variables.ContainsKey("queued"));
        game.Tick(TimeSpan.FromSeconds(1));
        Assert.IsTrue(game.Next()); Assert.AreEqual("yes", game.Execution.State.Variables["after"]);
    }
}
