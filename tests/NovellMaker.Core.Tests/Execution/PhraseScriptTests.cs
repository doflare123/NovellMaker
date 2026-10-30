using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class PhraseScriptTests
{
    [TestMethod]
    public void PhraseAutomaticallyShowsTextAndWaitsWithIndependentBeforeDuringAndAfterBlocks()
    {
        var (project, scene, actor) = Fixture(); var line = scene.Lines[0];
        scene = scene with { ScriptBlocks = [
            new(null, ScriptPhase.SceneStart, new SetAction(Id(), "start", "yes")),
            new(line.Id, ScriptPhase.AfterLine, new SetAction(Id(), "after", "yes")),
            new(line.Id, ScriptPhase.DuringLine, new MoveAction(Id(), actor.Id, new(0.8, 1, 1, 0), 2)),
            new(line.Id, ScriptPhase.BeforeLine, new ShowAction(Id(), actor.Id)),
            new(null, ScriptPhase.SceneEnd, new SetAction(Id(), "end", "yes")),
        ] };
        var player = Player(project, scene);
        Assert.AreEqual(line.Texts["ru"], player.Frame.Text); Assert.AreEqual("input", player.State.Wait?.Mode);
        Assert.AreEqual("yes", player.State.Variables["start"]); Assert.IsFalse(player.State.Variables.ContainsKey("after"));
        Assert.AreEqual(actor.Id, player.Frame.Characters.Single().CharacterId);
        player.TickSettled(TimeSpan.FromSeconds(1)); Assert.AreEqual(0.65, player.State.Placements[actor.Id].X, 1e-9);
        player.AdvanceSettled(); Assert.AreEqual("yes", player.State.Variables["after"]); Assert.IsFalse(player.State.Variables.ContainsKey("end"));
        player.AdvanceSettled(); Assert.AreEqual("yes", player.State.Variables["end"]); Assert.IsTrue(player.State.Completed);
    }

    [TestMethod]
    public void ReorderingPhrasesMovesTheirHooksWithoutChangingIdsOrTargets()
    {
        var (project, scene, _) = Fixture(); var first = scene.Lines[0]; var second = scene.Lines[1];
        var hook = new SetAction(Id(), "phase", "first");
        scene = scene with { ScriptBlocks = [new(first.Id, ScriptPhase.BeforeLine, hook), new(second.Id, ScriptPhase.BeforeLine, new SetAction(Id(), "phase", "second"))] };
        var entry = ScenarioProgram.LineEntryId(first.Id);
        scene = scene with { Lines = [second, first] };
        var player = Player(project, scene);
        Assert.AreEqual(second.Id, player.Frame.LineId); Assert.AreEqual("second", player.State.Variables["phase"]);
        player.AdvanceSettled(); Assert.AreEqual(first.Id, player.Frame.LineId); Assert.AreEqual("first", player.State.Variables["phase"]);
        Assert.IsTrue(ScenarioProgram.Compile(scene).Any(a => a.Id == hook.Id)); Assert.AreEqual(entry, ScenarioProgram.LineEntryId(first.Id));
    }

    [TestMethod]
    public void NamedLuaReferencesRemainStableAfterRenameAndJumpToPhraseRunsItsCues()
    {
        var (project, scene, actor) = Fixture();
        scene = scene with { ScriptBlocks = [new(scene.Lines[0].Id, ScriptPhase.AfterLine,
            new ScriptAction(Id(), "game.show(refs.actor)\ngame.jump_line(refs.scene, refs.next)", "next-phrase.lua")
            { References = ImmutableDictionary<string, Guid>.Empty.Add("actor", actor.Id).Add("scene", scene.Id).Add("next", scene.Lines[1].Id) }),
            new(scene.Lines[1].Id, ScriptPhase.BeforeLine, new SetAction(Id(), "entered", "yes"))] };
        project = project with { Characters = [actor with { Name = "Переименованный" }] };
        var player = Player(project, scene); player.AdvanceSettled();
        Assert.IsNull(player.State.Error, player.State.Error); Assert.AreEqual(scene.Lines[1].Id, player.Frame.LineId);
        Assert.AreEqual("yes", player.State.Variables["entered"]); Assert.AreEqual("Переименованный", player.Frame.Characters[0].Name);
    }

    [TestMethod]
    public async Task Version8RoundTripsBindingsAndVersion7KeepsItsOriginalProgramWithoutWriting()
    {
        var (project, scene, _) = Fixture();
        scene = scene with { ScriptBlocks = [new(scene.Lines[0].Id, ScriptPhase.DuringLine, new SetAction(Id(), "flag", "yes"))] };
        var path = Path.Combine(Path.GetTempPath(), $"phrase-{Id():N}.novell");
        try
        {
            var store = new JsonProjectStore(); project = project with { Scenes = [scene] }; await store.SaveAsync(path, project);
            var loaded = await store.LoadAsync(path); Assert.IsTrue(project.ContentEquals(loaded));
            var player = new ScenarioPlayer(loaded, scene.Id, "ru").Settled(); var save = GameSave.Serialize(player); player.AdvanceSettled(); GameSave.Load(player, save);
            Assert.AreEqual(scene.Lines[0].Id, player.Frame.LineId);
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!; node["formatVersion"] = 7; node.AsObject().Remove("releaseCheck"); node.AsObject().Remove("startScreenSceneId"); node.AsObject().Remove("inputBindings"); node.AsObject().Remove("screens"); node.AsObject().Remove("pauseScreenId"); node.AsObject().Remove("inputBindings"); if (node["scenes"] is JsonArray oldScenes) foreach (var oldScene in oldScenes) oldScene!.AsObject().Remove("canvas"); node.AsObject().Remove("audio"); node.AsObject().Remove("scripts");
            node["scenes"]![0]!.AsObject().Remove("scriptBlocks");
            await File.WriteAllTextAsync(path, node.ToJsonString()); var bytes = await File.ReadAllBytesAsync(path);
            loaded = await store.LoadAsync(path); Assert.IsTrue(loaded.Scenes[0].ScriptBlocks.IsEmpty);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            node["scenes"]![0]!["scriptBlocks"] = new JsonArray(); await File.WriteAllTextAsync(path, node.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow("line")]
    [DataRow("phase")]
    [DataRow("duplicate")]
    [DataRow("mixed")]
    public void InvalidBindingsAreRejectedBeforeSaving(string mutation)
    {
        var (project, scene, _) = Fixture(); var block = new ScriptBlock(scene.Lines[0].Id, ScriptPhase.DuringLine, new SetAction(Id(), "x", "1"));
        scene = scene with { ScriptBlocks = [block] };
        scene = mutation switch
        {
            "line" => scene with { ScriptBlocks = [block with { LineId = Id() }] },
            "phase" => scene with { ScriptBlocks = [block with { Phase = ScriptPhase.SceneStart }] },
            "duplicate" => scene with { ScriptBlocks = [block, block] },
            _ => scene with { UseActions = true },
        };
        Assert.Throws<ArgumentException>(() => ScenarioProgram.Validate(project with { Scenes = [scene] }));
    }

    [TestMethod]
    public async Task LegacyVersion7LuaProgramKeepsBehaviorAndRejectsVersion8LuaMetadata()
    {
        var (project, scene, actor) = Fixture();
        scene = scene with { UseActions = true, Actions = [new ScriptAction(Id(), "game.set('old', 'yes')", "old.lua"),
            new TextAction(Id(), scene.Lines[0].Id), new WaitAction(Id(), "input")] };
        project = project with { Scenes = [scene] };
        var path = Path.Combine(Path.GetTempPath(), $"legacy-script-{Id():N}.novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["formatVersion"] = 7; json.AsObject().Remove("releaseCheck"); json.AsObject().Remove("startScreenSceneId"); json.AsObject().Remove("inputBindings"); json.AsObject().Remove("screens"); json.AsObject().Remove("pauseScreenId"); json.AsObject().Remove("inputBindings"); if (json["scenes"] is JsonArray oldScenes) foreach (var oldScene in oldScenes) oldScene!.AsObject().Remove("canvas"); json.AsObject().Remove("audio"); json.AsObject().Remove("scripts"); json["scenes"]![0]!.AsObject().Remove("scriptBlocks");
            var script = json["scenes"]![0]!["actions"]![0]!.AsObject(); script.Remove("references");
            await File.WriteAllTextAsync(path, json.ToJsonString()); var bytes = await File.ReadAllBytesAsync(path);
            var migrated = await store.LoadAsync(path); Assert.IsTrue(project.ContentEquals(migrated with { Scenes = [migrated.Scenes[0] with { Canvas = scene.Canvas }] }));
            Assert.AreEqual(NovellMaker.Core.Screens.DialogueBinding.CssPanel, migrated.Scenes[0].Canvas.Nodes[1].Dialogue);
            Assert.AreEqual("yes", new ScenarioPlayer(migrated, scene.Id, "ru").Settled().State.Variables["old"]);
            CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
            script["references"] = new JsonObject { ["actor"] = actor.Id.ToString() };
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static Guid Id() => Guid.NewGuid();
    private static ScenarioPlayer Player(NovelProject project, NovelScene scene) => new ScenarioPlayer(project with { Scenes = [scene] }, scene.Id, "ru").Settled();
    private static (NovelProject, NovelScene, NovelCharacter) Fixture()
    {
        var image = new ImageAsset(Id(), "Спрайт", "Assets/Images/actor.png", 200, 400);
        var state = new CharacterState(Id(), "Обычный", image.Id);
        var actor = NovelCharacter.Create("Путник") with { States = [state], DefaultStateId = state.Id };
        var scene = NovelScene.Create("Сцена") with { Lines = [DialogueLine.Create("ru", "Первая"), DialogueLine.Create("ru", "Вторая")] };
        return (NovelProject.Create("Проект") with { Images = [image], Characters = [actor], Scenes = [scene] }, scene, actor);
    }
}
