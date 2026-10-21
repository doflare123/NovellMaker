using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Scenes;

[TestClass]
public sealed class UnifiedSceneTests
{
    [TestMethod]
    [DataRow(11)] [DataRow(12)]
    public async Task LegacyMenusBecomeScenesWithoutChangingIdsBindingsOrOriginalFile(int version)
    {
        var story = NovelScene.Create("Пролог") with { Lines = [DialogueLine.Create("ru", "Фраза")] };
        var menu = ScreenFixture.Create("Пролог"); // Same name is legal; identity remains the stable ID.
        var script = LuaScriptAsset.Create("menu.lua");
        menu = menu with { HandlerScriptId = script.Id, Buttons = [new(menu.Nodes[2].Id, ScreenButtonAction.Open, menu.Id)] };
        var project = NovelProject.Create("Новелла") with { Scenes = [story, menu.ToScene()], Scripts = [script], InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, menu.Id)] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            var oldMenu = JsonSerializer.SerializeToNode(menu, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            oldMenu.AsObject().Remove("pauseOnOverlay"); oldMenu.AsObject().Remove("blockInputOnOverlay");
            if (version == 11) foreach (var node in oldMenu["nodes"]!.AsArray()) { node!.AsObject().Remove("name"); node.AsObject().Remove("imageId"); }
            json["formatVersion"] = version; json.AsObject().Remove("inputBindings"); json["pauseScreenId"] = menu.Id;
            foreach (var binding in oldMenu["buttons"]!.AsArray()) { binding!.AsObject().Remove("group"); binding.AsObject().Remove("amount"); } json["screens"] = new JsonArray(oldMenu);
            json["scenes"]!.AsArray().RemoveAt(1); json["scenes"]![0]!.AsObject().Remove("canvas");
            ScreenFixture.RemoveGeometry(json);
            File.WriteAllText(path, json.ToJsonString()); var original = File.ReadAllBytes(path);
            var migrated = await store.LoadAsync(path);
            Assert.HasCount(2, migrated.Scenes); Assert.IsTrue(story.ContentEquals(migrated.Scenes[0]));
            Assert.IsTrue(menu.ContentEquals(migrated.Scenes[1].AsScreen()));
            Assert.AreEqual(menu.Id, migrated.InputBindings.FirstOrDefault(b => b.Key == "P")?.TargetSceneId);
            Assert.AreEqual(ScenarioProgram.Fingerprint(migrated), ScenarioProgram.Fingerprint(await store.LoadAsync(path)));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            json["screens"]![0]!["nodes"]![1]!["kind"] = 5; File.WriteAllText(path, json.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            await store.SaveAsync(path, migrated);
            var current = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.IsFalse(current.AsObject().ContainsKey("screens")); Assert.IsNotNull(current["scenes"]![0]!["canvas"]);
            Assert.IsTrue(migrated.ContentEquals(await store.LoadAsync(path)));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task UnifiedSchemaRequiresCanvasAndPoliciesAndRejectsLegacyScreenCollection()
    {
        var project = NovelProject.Create("Новелла") with { Scenes = [NovelScene.Create("Сцена")] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project); var json = File.ReadAllText(path);
            foreach (var field in new[] { "canvas", "nodes", "pauseOnOverlay", "blockInputOnOverlay" })
            {
                var doc = JsonNode.Parse(json)!; var scene = doc["scenes"]![0]!;
                (field == "canvas" ? scene : scene["canvas"]!).AsObject().Remove(field);
                File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            }
            var extra = JsonNode.Parse(json)!; extra["screens"] = new JsonArray(); File.WriteAllText(path, extra.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void CopySceneRemapsDialogueEventsNodesSelfTargetsAndOwnRefsTogether()
    {
        var source = ScreenFixture.Create("Общая сцена").ToScene(); var line = DialogueLine.Create("ru", "Текст");
        var jump = new JumpAction(Guid.NewGuid(), source.Id, ScenarioProgram.LineEntryId(line.Id));
        var call = new ScriptAction(Guid.NewGuid(), "", "Lua") { References = ImmutableDictionary<string, Guid>.Empty.Add("line", line.Id).Add("scene", source.Id) };
        source = source with { Lines = [line], ScriptBlocks = [new(line.Id, ScriptPhase.BeforeLine, jump), new(null, ScriptPhase.SceneStart, call)],
            Canvas = source.Canvas with { Buttons = [new(source.Canvas.Nodes[2].Id, ScreenButtonAction.Scene, source.Id)] } };
        var (copy, ids) = SceneCopy.Create(source);
        Assert.AreNotEqual(source.Id, copy.Id); Assert.AreEqual(ids[line.Id], copy.Lines.Single().Id);
        Assert.AreEqual(copy.Lines.Single().Id, copy.ScriptBlocks[0].LineId);
        var copiedJump = (JumpAction)copy.ScriptBlocks[0].Action;
        Assert.AreEqual(copy.Id, copiedJump.SceneId); Assert.AreEqual(ScenarioProgram.LineEntryId(copy.Lines[0].Id), copiedJump.ActionId);
        Assert.AreEqual(copy.Id, ((ScriptAction)copy.ScriptBlocks[1].Action).References["scene"]);
        Assert.AreEqual(copy.Canvas.Nodes[1].Id, copy.Canvas.References["title"]);
        Assert.AreEqual(copy.Id, copy.Canvas.Buttons.Single().TargetScreenId);
        Assert.AreEqual(source.Canvas.Buttons[0].NodeId, source.Canvas.Nodes[2].Id);
    }

    [TestMethod]
    public void CopyImportedFlowRemapsEveryLocalTargetAndPreservesExternalResources()
    {
        var source = ScreenFixture.Create("Импортированный поток").ToScene(); var line = DialogueLine.Create("ru", "Фраза");
        var entry = Guid.NewGuid(); var objectId = Guid.NewGuid(); var externalScene = Guid.NewGuid(); var externalAction = Guid.NewGuid(); var script = Guid.NewGuid();
        source = source with { Lines = [line], UseActions = true, Actions =
        [
            new LineEntryAction(entry, line.Id), new TextAction(Guid.NewGuid(), line.Id),
            new BranchAction(Guid.NewGuid(), "key", "value", source.Id, entry),
            new ChoiceAction(Guid.NewGuid(), [new(Guid.NewGuid(), "Своя", source.Id, entry), new(Guid.NewGuid(), "Другая", externalScene, externalAction)]),
            new HotspotAction(objectId, objectId, "Область", 0, 0, .2, .2),
            new WaitAction(Guid.NewGuid(), "interaction", Token: objectId), new WaitAction(Guid.NewGuid(), "input"),
            new RunScriptAction(Guid.NewGuid(), script), new JumpAction(Guid.NewGuid(), externalScene, externalAction)
        ] };
        var (copy, ids) = SceneCopy.Create(source);
        Assert.AreEqual(copy.Lines[0].Id, ((LineEntryAction)copy.Actions[0]).LineId);
        Assert.AreEqual(copy.Lines[0].Id, ((TextAction)copy.Actions[1]).LineId);
        Assert.AreEqual(ids[entry], ((BranchAction)copy.Actions[2]).ActionId);
        Assert.AreEqual(copy.Id, ((BranchAction)copy.Actions[2]).SceneId);
        var choice = (ChoiceAction)copy.Actions[3]; Assert.AreEqual(copy.Id, choice.Options[0].TargetSceneId);
        Assert.AreEqual(ids[entry], choice.Options[0].TargetActionId); Assert.AreEqual(externalAction, choice.Options[1].TargetActionId);
        Assert.AreEqual(externalScene, choice.Options[1].TargetSceneId);
        Assert.AreNotEqual(((ChoiceAction)source.Actions[3]).Options[0].Id, choice.Options[0].Id);
        Assert.AreEqual(ids[objectId], ((HotspotAction)copy.Actions[4]).ObjectId);
        Assert.AreEqual(ids[objectId], ((WaitAction)copy.Actions[5]).Token); Assert.IsNull(((WaitAction)copy.Actions[6]).Token);
        Assert.AreEqual(script, ((RunScriptAction)copy.Actions[7]).ScriptId);
        Assert.AreEqual(externalScene, ((JumpAction)copy.Actions[8]).SceneId); Assert.AreEqual(externalAction, ((JumpAction)copy.Actions[8]).ActionId);
    }

    [TestMethod]
    public void EnterSceneStartsOncePreservesVariablesAndSupportsSceneWithoutDialogue()
    {
        var story = NovelScene.Create("Сюжет") with { Lines = [DialogueLine.Create("ru", "Первая")],
            ScriptBlocks = [new(null, ScriptPhase.SceneStart, new SetAction(Guid.NewGuid(), "value", "yes"))] };
        var menu = ScreenFixture.Create("Меню").ToScene();
        var project = NovelProject.Create("Новелла") with { Scenes = [menu, story] };
        using var player = new ScenePlayer(project, menu.Id, "ru", mode: PlaybackMode.Game);
        Assert.IsTrue(player.Execution.State.Completed); Assert.IsTrue(player.EnterScene(story.Id));
        Assert.AreEqual(story.Lines[0].Id, player.Frame.LineId); Assert.AreEqual("yes", player.Execution.State.Variables["value"]);
        var state = player.Execution.State; Assert.IsFalse(player.EnterScene(Guid.NewGuid())); Assert.AreSame(state, player.Execution.State);
        Assert.IsTrue(player.EnterScene(menu.Id)); Assert.IsTrue(player.Execution.State.Completed); Assert.AreEqual("yes", player.Execution.State.Variables["value"]);
        var save = GameSave.Serialize(player.Execution); Assert.IsTrue(player.EnterScene(story.Id)); player.LoadSave(save);
        Assert.AreEqual(menu.Id, player.Execution.State.SceneId); Assert.IsTrue(player.Execution.State.Completed);
    }
}
