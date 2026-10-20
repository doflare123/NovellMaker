using System.Collections.Immutable;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

/// <summary>Behaviour that the asynchronous transport must preserve.</summary>
[TestClass]
public sealed class LuaCompatibilityTests
{
    [TestMethod]
    [DataRow("timer")]
    [DataRow("actions")]
    [DataRow("interaction")]
    [DataRow("choice")]
    public void UnrelatedInputCannotConsumeAnotherWait(string kind)
    {
        var (project, scene, actor) = Fixture();
        var hotspot = Guid.NewGuid();
        var option = Guid.NewGuid();
        var tail = new SetAction(Guid.NewGuid(), "finished", "yes");
        ScenarioAction[] prefix = kind switch
        {
            "timer" => [new WaitAction(Guid.NewGuid(), "timer", 2)],
            "actions" => [new MoveAction(Guid.NewGuid(), actor.Id, new(0.8, 1, 1, 0), 2), new WaitAction(Guid.NewGuid(), "actions")],
            "interaction" => [new HotspotAction(Guid.NewGuid(), hotspot, "Выбрать", 0.1, 0.1, 0.2, 0.2), new WaitAction(Guid.NewGuid(), "interaction", Token: hotspot)],
            _ => [new ChoiceAction(Guid.NewGuid(), [new(option, "Выбор", scene.Id, tail.Id)])]
        };
        scene = scene with { UseActions = true, Actions = [.. prefix, tail, new WaitAction(Guid.NewGuid(), "input")] };
        project = project with { Scenes = [scene] };
        ScenarioProgram.Validate(project);
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        var before = GameSave.Serialize(player);
        Assert.IsFalse(player.AdvanceSettled());
        Assert.IsFalse(player.ClickSettled(Guid.NewGuid()));
        Assert.IsFalse(player.ChooseSettled(Guid.NewGuid()));
        Assert.AreEqual(before, GameSave.Serialize(player));
        switch (kind)
        {
            case "timer": case "actions": player.TickSettled(TimeSpan.FromSeconds(2)); break;
            case "interaction": Assert.IsTrue(player.ClickSettled(hotspot)); break;
            default: Assert.IsTrue(player.ChooseSettled(option)); break;
        }
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.AreEqual("yes", player.State.Variables["finished"]);
    }

    [TestMethod]
    public void GlobalsAndReferencesAreFreshWhileExplicitVariablesPersist()
    {
        var (project, scene, _) = Fixture();
        var first = new LuaScriptAsset(Guid.NewGuid(), "first.lua", "private_marker = 'old'; game.set('persisted', 'yes'); game.set('first', refs.self)");
        first = first with { References = ImmutableDictionary<string, Guid>.Empty.Add("self", first.Id) };
        var second = new LuaScriptAsset(Guid.NewGuid(), "second.lua",
            "if private_marker == nil and refs.self == nil and game.get('persisted') == 'yes' then game.set('isolated', 'yes') end");
        scene = scene with { ScriptBlocks = [
            new(null, ScriptPhase.SceneStart, new RunScriptAction(Guid.NewGuid(), first.Id)),
            new(null, ScriptPhase.SceneStart, new RunScriptAction(Guid.NewGuid(), second.Id))] };
        var player = new ScenarioPlayer(project with { Scenes = [scene], Scripts = [first, second] }, scene.Id, "ru").Settled();
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.AreEqual(first.Id.ToString(), player.State.Variables["first"]);
        Assert.AreEqual("yes", player.State.Variables["isolated"]);
    }

    [TestMethod]
    public void FailedDeferredChildKeepsItsPositionAndEarlierSuccessfulCommands()
    {
        var (project, scene, _) = Fixture();
        var child = new LuaScriptAsset(Guid.NewGuid(), "child.lua", "game.set('partial', 'bad'); missing_function()");
        var parent = new ScriptAction(Guid.NewGuid(), "game.set('before', 'yes'); game.wait(1); game.run(refs.child); game.set('tail', 'bad')", "parent.lua")
        { References = ImmutableDictionary<string, Guid>.Empty.Add("child", child.Id) };
        scene = scene with { ScriptBlocks = [new(null, ScriptPhase.SceneStart, parent)] };
        var player = new ScenarioPlayer(project with { Scenes = [scene], Scripts = [child] }, scene.Id, "ru").Settled();
        var queuedChild = player.State.QueuedActions[0];
        Assert.IsInstanceOfType<RunScriptAction>(queuedChild);
        player.TickSettled(TimeSpan.FromSeconds(1));
        Assert.IsNotNull(player.State.Error);
        Assert.AreEqual("yes", player.State.Variables["before"]);
        Assert.IsFalse(player.State.Variables.ContainsKey("partial"));
        Assert.IsFalse(player.State.Variables.ContainsKey("tail"));
        Assert.AreEqual(queuedChild.Id, player.State.QueuedActions[0].Id);
        var failed = player.State;
        player.TickSettled(TimeSpan.FromSeconds(10));
        Assert.IsFalse(player.AdvanceSettled());
        Assert.AreSame(failed, player.State);
    }

    [TestMethod]
    public void RestorePreservesQueuedCommandIdentitiesAndEmitsOnlyRestoreAudio()
    {
        var (project, scene, _) = Fixture();
        scene = scene with { ScriptBlocks = [new(null, ScriptPhase.SceneStart,
            new ScriptAction(Guid.NewGuid(), "game.wait(2); game.set('tail', 'once')", "save.lua"))] };
        var player = new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru").Settled();
        player.TickSettled(TimeSpan.FromSeconds(0.5));
        var saved = GameSave.Serialize(player);
        var ids = player.State.QueuedActions.Select(a => a.Id).ToArray();
        player.TickSettled(TimeSpan.FromSeconds(1.5));
        Assert.AreEqual("once", player.State.Variables["tail"]);
        GameSave.Load(player, saved);
        Assert.AreEqual(saved, GameSave.Serialize(player));
        CollectionAssert.AreEqual(ids, player.State.QueuedActions.Select(a => a.Id).ToArray());
        var cues = player.DrainAudioEvents();
        Assert.HasCount(1, cues);
        Assert.AreEqual(AudioEventKind.Restore, cues[0].Kind);
        Assert.IsFalse(player.State.Variables.ContainsKey("tail"));
    }

    [TestMethod]
    public void ObjectTableIsASnapshotAndLegacyFinishDoesNotCloseOrCleanObjects()
    {
        var (project, scene, actor) = Fixture();
        var hotspot = Guid.NewGuid();
        scene = scene with { ScriptBlocks = [new(null, ScriptPhase.SceneStart,
            new ScriptAction(Guid.NewGuid(), $$"""
                game.place(refs.actor, 0.2, 1, 1, 0)
                local snapshot = game.object(refs.actor)
                snapshot.x = 0.9
                game.hotspot('{{hotspot}}', 'Кнопка', 0.1, 0.1, 0.2, 0.2)
                game.finish('win')
                game.wait_input()
                """, "legacy.lua") { References = ImmutableDictionary<string, Guid>.Empty.Add("actor", actor.Id) })] };
        var player = new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru").Settled();
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.AreEqual(0.2, player.State.Placements[actor.Id].X);
        Assert.AreEqual("win", player.State.Variables["minigame.result"]);
        Assert.AreEqual("input", player.State.Wait?.Mode);
        Assert.AreEqual(hotspot, player.State.Hotspots.Single().ObjectId);
        Assert.IsFalse(player.State.Completed);
    }

    [TestMethod]
    public async Task CancelledSavePreservesPreviousSlotAndLeavesNoTemporaryFile()
    {
        var (project, scene, _) = Fixture();
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        var directory = Path.Combine(Path.GetTempPath(), "novell-slot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "slot.json");
        try
        {
            await File.WriteAllTextAsync(path, "previous-slot");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => GameSave.SaveAsync(path, player, cancellation.Token));
            Assert.AreEqual("previous-slot", await File.ReadAllTextAsync(path));
            CollectionAssert.AreEqual(new[] { "slot.json" }, Directory.GetFiles(directory).Select(Path.GetFileName).ToArray());
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    private static (NovelProject Project, NovelScene Scene, NovelCharacter Actor) Fixture()
    {
        var image = new ImageAsset(Guid.NewGuid(), "Спрайт", "Assets/Images/actor.png", 300, 600);
        var pose = new CharacterState(Guid.NewGuid(), "Обычное", image.Id);
        var actor = NovelCharacter.Create("Персонаж") with { States = [pose], DefaultStateId = pose.Id };
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Реплика")] };
        return (NovelProject.Create("Контракты") with { Scenes = [scene], Images = [image], Characters = [actor] }, scene, actor);
    }
}
