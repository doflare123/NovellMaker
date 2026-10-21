using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class ScenarioPlayerTests
{
    [TestMethod]
    public void NonSpeakerMovesWhileTextWaits_AndStateChangesDoNotShowObjects()
    {
        var (project, scene, actor, line) = Fixture();
        scene = scene with { Actions = [new StateAction(Id(), actor.Id, actor.DefaultStateId!.Value),
            new PlaceAction(Id(), actor.Id, new(0.2, 1, 1, 0)), new ShowAction(Id(), actor.Id),
            new MoveAction(Id(), actor.Id, new(0.8, 0.6, 2, 2), 4), new TextAction(Id(), line.Id), new WaitAction(Id(), "input")] };
        var player = Player(project, scene);
        var snapshot = player.State;
        player.TickSettled(TimeSpan.FromSeconds(1));
        Assert.AreEqual(line.Id, player.State.LineId);
        Assert.AreEqual("Рассказчик", player.Frame.Text);
        Assert.AreEqual("", player.Frame.SpeakerName);
        Assert.AreEqual(0.35, player.State.Placements[actor.Id].X, 1e-9);
        Assert.AreEqual(0.2, snapshot.Placements[actor.Id].X);
        Assert.AreEqual("input", player.State.Wait?.Mode);
        player.TickSettled(TimeSpan.FromSeconds(3));
        Assert.IsTrue(player.State.ActiveActions.IsEmpty);
        Assert.AreEqual(0.8, player.State.Placements[actor.Id].X);
        Assert.IsTrue(player.AdvanceSettled()); Assert.IsTrue(player.State.Completed);
    }

    [TestMethod]
    public void LargeAndSmallTicksAgreeAcrossTimerAndActionWaits()
    {
        var (project, scene, actor, _) = Fixture();
        scene = scene with { Actions = [new MoveAction(Id(), actor.Id, new(0.8, 1, 1, 0), 4),
            new WaitAction(Id(), "timer", 1), new SetAction(Id(), "first", "yes"), new WaitAction(Id(), "actions"),
            new SetAction(Id(), "done", "yes"), new WaitAction(Id(), "input")] };
        var big = Player(project, scene); var small = Player(project, scene);
        big.TickSettled(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 20; i++) small.TickSettled(TimeSpan.FromSeconds(0.25));
        Assert.AreEqual(GameSave.Serialize(big), GameSave.Serialize(small));
        Assert.AreEqual("yes", big.State.Variables["done"]);
    }

    [TestMethod]
    public void SaveRestoresMotionWaitVariablesVisibilityAndQueuedLuaCommandsWithoutRerun()
    {
        var (project, scene, actor, line) = Fixture();
        var hotspot = Id();
        scene = scene with { Actions = [new ShowAction(Id(), actor.Id), new PlaceAction(Id(), actor.Id, new(0.2, 1, 1, 0)),
            new MoveAction(Id(), actor.Id, new(0.8, 1, 1, 0), 4), new TextAction(Id(), line.Id),
            new ScriptAction(Id(), $$"""
                game.set("phase", "started")
                game.hotspot("{{hotspot}}", "Предмет", 0.4, 0.3, 0.2, 0.1)
                game.wait_click()
                game.finish("win")
                game.wait(2)
                game.set("phase", "done")
                """, "restore.lua")] };
        project = project with { Scenes = [scene] };
        var original = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        original.TickSettled(TimeSpan.FromSeconds(1));
        Assert.IsNull(original.State.Error, original.State.Error);
        Assert.AreEqual(3, original.State.QueuedActions.Length);
        var restored = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        GameSave.Load(restored, GameSave.Serialize(original));
        Assert.AreEqual(GameSave.Serialize(original), GameSave.Serialize(restored));
        Assert.IsFalse(restored.ClickSettled(Id()));
        Assert.IsTrue(original.ClickSettled(hotspot)); Assert.IsTrue(restored.ClickSettled(hotspot));
        Assert.AreEqual("win", restored.State.Variables["minigame.result"]);
        var timedSave = GameSave.Serialize(restored);
        restored.TickSettled(TimeSpan.FromSeconds(100));
        GameSave.Load(restored, timedSave);
        original.TickSettled(TimeSpan.FromSeconds(2)); restored.TickSettled(TimeSpan.FromSeconds(2));
        Assert.AreEqual(GameSave.Serialize(original), GameSave.Serialize(restored));
        Assert.AreEqual("done", restored.State.Variables["phase"]);
    }

    [TestMethod]
    public void ChoicesAndBranchingUseStableActionIdsAcrossScenes()
    {
        var (project, scene, _, _) = Fixture();
        var destination = NovelScene.Create("Финал"); var target = Id(); var option = Id();
        destination = destination with { Actions = [new SetAction(target, "arrived", "yes"), new WaitAction(Id(), "input")] };
        scene = scene with { Actions = [new ChoiceAction(Id(), [new(option, "Перейти", destination.Id, target)])] };
        project = project with { Scenes = [scene, destination] };
        ScenarioProgram.Validate(project);
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        var json = GameSave.Serialize(player); Assert.IsFalse(player.AdvanceSettled()); Assert.IsFalse(player.ChooseSettled(Id()));
        Assert.IsTrue(player.ChooseSettled(option)); Assert.AreEqual(destination.Id, player.State.SceneId);
        Assert.AreEqual("yes", player.State.Variables["arrived"]);
        GameSave.Load(player, json); Assert.IsNotNull(player.State.Choice);
        Assert.IsTrue(player.ChooseSettled(option));
    }

    [TestMethod]
    public void LuaUsesSameActionsAndReturnsMinigameResult()
    {
        var (project, scene, actor, line) = Fixture(); var target = Id();
        scene = scene with { Actions = [new ScriptAction(Id(), $$"""
            game.set("flag", "ok")
            if game.get("flag") == "ok" then
                game.show("{{actor.Id}}")
                game.move("{{actor.Id}}", 0.8, 1, 1, 0, 2)
                game.text("{{line.Id}}")
                game.finish("win")
                game.wait_input()
            end
            """, "commands.lua"), new BranchAction(Id(), "minigame.result", "win", scene.Id, target),
            new SetAction(Id(), "bad", "yes"), new SetAction(target, "good", "yes")] };
        var player = Player(project, scene);
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.HasCount(1, player.Frame.Characters);
        player.TickSettled(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0.65, player.State.Placements[actor.Id].X, 1e-9);
        player.AdvanceSettled(); Assert.AreEqual("yes", player.State.Variables["good"]); Assert.IsFalse(player.State.Variables.ContainsKey("bad"));
    }

    [TestMethod]
    [DataRow("while true do end")]
    [DataRow("game.set('bad', 'yes'); while true do end")]
    [DataRow("io.open('file')")]
    [DataRow("os.execute('x')")]
    [DataRow("require('x')")]
    [DataRow("clr.import('System')")]
    public void LuaFailureIsBoundedDiagnosticAndDoesNotCommitPartialBatch(string source)
    {
        var (project, scene, _, _) = Fixture();
        scene = scene with { Actions = [new ScriptAction(Id(), source, "broken.lua")] };
        var player = Player(project, scene);
        Assert.IsNotNull(player.State.Error);
        StringAssert.Contains(player.State.Error, "broken.lua");
        Assert.IsFalse(player.State.Variables.ContainsKey("bad"));
        Assert.IsFalse(player.AdvanceSettled());
    }

    [TestMethod]
    public void LuaErrorsIncludeScriptAndLine()
    {
        var (project, scene, _, _) = Fixture();
        var player = Player(project, scene with { Actions = [new ScriptAction(Id(), "local a = 1\nmissing()", "line-error.lua")] });
        StringAssert.Contains(player.State.Error!, "line-error.lua");
        StringAssert.Contains(player.State.Error!, "2");
    }

    [TestMethod]
    public void InvalidLuaCommandIncludesSourceLineAndDiscardsWholeBatch()
    {
        var (project, scene, actor, _) = Fixture();
        var player = Player(project, scene with { Actions = [new ScriptAction(Id(),
            $"game.set('flag', 'yes')\ngame.move('{actor.Id}', 99, 1, 1, 0, 2)", "bad-parameter.lua")] });
        StringAssert.Contains(player.State.Error!, "bad-parameter.lua");
        StringAssert.Contains(player.State.Error!, "2");
        Assert.IsFalse(player.State.Variables.ContainsKey("flag"));
        Assert.IsTrue(player.State.QueuedActions.IsEmpty);
    }

    [TestMethod]
    public void InfiniteScenarioJumpFailsWithoutBlockingHost()
    {
        var (project, scene, _, _) = Fixture(); var id = Id();
        var player = Player(project, scene with { Actions = [new JumpAction(id, scene.Id, id)] });
        Assert.IsNotNull(player.State.Error);
    }

    [TestMethod]
    public void BadSaveAndDifferentRevisionLeaveLiveStateUntouched()
    {
        var (project, scene, _, _) = Fixture();
        var player = Player(project, scene with { Actions = [new WaitAction(Id(), "input")] });
        var saved = GameSave.Serialize(player); var before = player.State;
        foreach (var mutation in new Action<JsonNode>[] {
            n => n["Version"] = 999,
            n => n["State"]!["Revision"] = "different",
            n => n["State"]!["NextActionId"] = Id().ToString(),
            n => n["State"]!["Placements"] = null,
            n => n["State"]!["Wait"]!["Mode"] = "unknown",
            n => n["State"]!["Time"] = -1,
            n => n["State"]!["Time"] = double.MaxValue,
            n => n["State"]!["VisibleObjects"] = new JsonArray(Id().ToString()),
            n => n["State"]!.AsObject().Remove("Time"),
        })
        {
            var node = JsonNode.Parse(saved)!; mutation(node);
            Assert.Throws<ArgumentException>(() => GameSave.Load(player, node.ToJsonString()));
            Assert.AreSame(before, player.State);
        }
    }

    [TestMethod]
    public async Task Version7RoundTripsActionsAndOldVersion6MigratesWithoutWriting()
    {
        var (project, scene, _, line) = Fixture();
        scene = scene with { Actions = [new TextAction(Id(), line.Id), new WaitAction(Id(), "input")] };
        project = project with { Scenes = [scene] };
        var path = Path.Combine(Path.GetTempPath(), $"scenario-{Id():N}.novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var loaded = await store.LoadAsync(path); Assert.IsTrue(project.ContentEquals(loaded));
            Assert.AreEqual(ScenarioProgram.Fingerprint(project), ScenarioProgram.Fingerprint(loaded));
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            node["formatVersion"] = 6; node.AsObject().Remove("inputBindings"); node.AsObject().Remove("screens"); node.AsObject().Remove("pauseScreenId"); node.AsObject().Remove("inputBindings"); if (node["scenes"] is JsonArray oldScenes) foreach (var oldScene in oldScenes) oldScene!.AsObject().Remove("canvas"); node.AsObject().Remove("audio"); node.AsObject().Remove("scripts"); node["scenes"]![0]!.AsObject().Remove("actions"); node["scenes"]![0]!.AsObject().Remove("useActions"); node["scenes"]![0]!.AsObject().Remove("scriptBlocks");
            await File.WriteAllTextAsync(path, node.ToJsonString()); var bytes = await File.ReadAllBytesAsync(path);
            loaded = await store.LoadAsync(path); Assert.IsTrue(loaded.Scenes[0].Actions.IsEmpty);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            node["scenes"]![0]!["actions"] = new JsonArray(); await File.WriteAllTextAsync(path, node.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void LuaChoiceAndPlacementUseDocumentedApiAndCanBeCancelled()
    {
        var (project, scene, actor, _) = Fixture(); var target = Id(); var option = Id();
        scene = scene with { Actions = [new ScriptAction(Id(), $$"""
            game.place("{{actor.Id}}", 0.3, 1, 1, 0)
            game.state("{{actor.Id}}", "{{actor.DefaultStateId}}")
            game.show("{{actor.Id}}")
            game.choice({ { id = "{{option}}", text = "Дальше", scene = "{{scene.Id}}", action = "{{target}}" } })
            """, "choice.lua"), new WaitAction(target, "input")] };
        var player = Player(project, scene);
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.AreEqual(0.3, player.State.Placements[actor.Id].X);
        var save = GameSave.Serialize(player); player.ChooseSettled(option); GameSave.Load(player, save);
        Assert.IsNotNull(player.State.Choice); Assert.IsTrue(player.ChooseSettled(option));
        player.Cancel(); var cancelled = player.State;
        Assert.IsFalse(player.AdvanceSettled()); player.TickSettled(TimeSpan.FromSeconds(3)); Assert.AreSame(cancelled, player.State);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("duplicate")]
    [DataRow("unknown-kind")]
    [DataRow("missing-field")]
    [DataRow("unknown-field")]
    [DataRow("global-id")]
    public async Task Version7StrictlyRejectsMalformedActions(string mutation)
    {
        var (project, scene, _, line) = Fixture();
        scene = scene with { Actions = [new TextAction(Id(), line.Id), new WaitAction(Id(), "input")] };
        var path = Path.Combine(Path.GetTempPath(), $"actions-invalid-{Id():N}.novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project with { Scenes = [scene] });
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var actions = json["scenes"]![0]!["actions"]!.AsArray();
            switch (mutation)
            {
                case "null": actions[0] = null; break;
                case "duplicate": actions.Add(actions[0]!.DeepClone()); break;
                case "unknown-kind": actions[0]!["kind"] = "future"; break;
                case "missing-field": actions[0]!.AsObject().Remove("lineId"); break;
                case "unknown-field": actions[0]!["future"] = true; break;
                case "global-id": actions[0]!["id"] = scene.Id.ToString(); break;
            }
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ExplicitEmptyScriptDoesNotResurrectDialogueExecution()
    {
        var (project, scene, _, _) = Fixture();
        var player = Player(project, scene with { UseActions = true });
        Assert.IsTrue(player.State.Completed); Assert.IsNull(player.State.LineId);
    }

    private static Guid Id() => Guid.NewGuid();
    private static ScenarioPlayer Player(NovelProject project, NovelScene scene) => new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru").Settled();
    private static (NovelProject Project, NovelScene Scene, NovelCharacter Actor, DialogueLine Line) Fixture()
    {
        var image = new ImageAsset(Id(), "Спрайт", "Assets/Images/a.png", 300, 600);
        var pose = new CharacterState(Id(), "Обычно", image.Id);
        var actor = NovelCharacter.Create("Персонаж") with { States = [pose], DefaultStateId = pose.Id };
        var line = DialogueLine.Create("ru", "Рассказчик");
        var scene = NovelScene.Create("Начало") with { Lines = [line] };
        return (NovelProject.Create("Тест") with { Images = [image], Characters = [actor], Scenes = [scene] }, scene, actor, line);
    }
}
