using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ScreenAssetTests
{
    private static NovelProject Fixture()
    {
        var screen = ScreenFixture.Create("Пауза автора");
        var script = new LuaScriptAsset(Guid.NewGuid(), "menu.lua", "return {on_event=function() ui.close() end}");
        return NovelProject.Create("Меню") with { Scenes = [screen.ToScene() with { Canvas = SceneCanvas.FromScreen(screen with { HandlerScriptId = script.Id }), Lines = [DialogueLine.Create("ru", "Фраза")] }], Scripts = [script], InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, screen.Id)] };
    }

    [TestMethod]
    public async Task RoundTripIncludesBindingsAndRefsAndEveryEditChangesSaveRevision()
    {
        var project = Fixture(); var screen = project.Screens[0]; var other = ScreenFixture.Create("Подтверждение");
        screen = screen with { Buttons = screen.Buttons.Add(new(screen.Nodes[2].Id, ScreenButtonAction.Open, other.Id)) .RemoveAt(0) };
        project = project with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[screen, other]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()};
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var loaded = await store.LoadAsync(path); Assert.IsTrue(project.ContentEquals(loaded));
            Assert.AreEqual(other.Id, loaded.Screens[0].Buttons.Last().TargetScreenId);
            using var player = new ScenarioPlayer(project, project.Scenes[0].Id, "ru");
            var save = GameSave.Serialize(player);
            var edits = new[] {
                project with { InputBindings = [] }, project with { Scenes = [NovelScene.Create("Другая сцена")], InputBindings = [] },
                project with { Scenes = project.Screens.SetItem(0, screen with { Name = "Новое имя" }).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
                project with { Scenes = project.Screens.SetItem(0, screen with { HandlerScriptId = null }).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
                project with { Scenes = project.Screens.SetItem(0, screen with { References = screen.References.Remove("title") }).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
                project with { Scenes = project.Screens.SetItem(0, screen with { Nodes = screen.Nodes.SetItem(1, screen.Nodes[1] with { Text = "Новый заголовок" }) }).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
                project with { Scenes = project.Screens.SetItem(0, screen with { Buttons = [] }).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
                project with { Scripts = [project.Scripts[0] with { Source = "return {on_event=function() end}" }] }
            };
            foreach (var edited in edits)
            {
                Assert.IsFalse(project.ContentEquals(edited)); Assert.AreNotEqual(ScenarioProgram.Fingerprint(project), ScenarioProgram.Fingerprint(edited));
                using var changed = new ScenarioPlayer(edited, edited.Scenes[0].Id, "ru");
                Assert.Throws<ArgumentException>(() => GameSave.Load(changed, save));
            }
            Assert.IsFalse(project.ContentEquals(project with { Scenes = default}));
            Assert.IsTrue(screen.ContentEquals(screen with { References = screen.References.Reverse().ToImmutableDictionary() }));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)]
    [DataRow(6)] [DataRow(7)] [DataRow(8)] [DataRow(9)] [DataRow(10)]
    public async Task EveryOldVersionMigratesWithoutWritingAndRejectsScreenFields(int version)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, NovelProject.Create("Старый проект"));
            var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); doc["formatVersion"] = version; doc.AsObject().Remove("releaseCheck"); doc.AsObject().Remove("startScreenSceneId");
            doc.Remove("inputBindings"); doc.Remove("screens"); doc.Remove("pauseScreenId"); doc.AsObject().Remove("inputBindings");
            if (doc["scenes"] is JsonArray legacyScenes) foreach (var legacyScene in legacyScenes) legacyScene!.AsObject().Remove("canvas");
            if (version < 10) doc.Remove("scripts");
            if (version < 9) doc.Remove("audio");
            if (version < 6) doc.Remove("explorerShowsFiles");
            if (version < 4) doc.Remove("characters");
            if (version < 3) doc.Remove("images");
            if (version < 2) { doc.Remove("scenes"); doc.Remove("defaultLanguage"); doc.Remove("dialogueUi"); }
            File.WriteAllText(path, doc.ToJsonString()); var original = File.ReadAllBytes(path);
            var migrated = await store.LoadAsync(path);
            Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion); Assert.IsEmpty(migrated.Screens); Assert.IsNull(migrated.InputBindings.FirstOrDefault(b => b.Key == "P")?.TargetSceneId);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            doc["screens"] = new JsonArray(); File.WriteAllText(path, doc.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            doc.Remove("inputBindings"); doc.Remove("screens"); doc["pauseScreenId"] = null; File.WriteAllText(path, doc.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)]
    [DataRow(7)] [DataRow(8)] [DataRow(9)] [DataRow(10)] [DataRow(11)] [DataRow(12)] [DataRow(13)]
    [DataRow(14)] [DataRow(15)] [DataRow(16)] [DataRow(17)] [DataRow(18)] [DataRow(19)] [DataRow(20)]
    public async Task InvalidResourcesAreRejectedAtomically(int mode)
    {
        var valid = Fixture(); var s = valid.Screens[0]; var button = s.Nodes[2].Id;
        var invalid = mode switch
        {
            0 => valid with { Scenes = default}, 1 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[null!]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            2 => valid with { InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, Guid.Empty)] }, 3 => valid with { InputBindings = [new("P", NovellMaker.Core.Screens.ScreenButtonAction.Toggle, Guid.NewGuid())] },
            4 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Name = " " }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            5 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Id = valid.Id }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            6 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { HandlerScriptId = Guid.NewGuid() }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            7 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { References = null! }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            8 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { References = s.References.Add("bad alias", button) }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            9 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { References = s.References.Add("foreign", valid.Scripts[0].Id) }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            10 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = default }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            11 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = [null!] }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            12 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = [new(button, (ScreenButtonAction)999, null)] }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            13 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = [new(s.Nodes[1].Id, ScreenButtonAction.Close, null)] }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            14 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = [new(button, ScreenButtonAction.Open, Guid.NewGuid())] }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            15 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = [new(button, ScreenButtonAction.Close, s.Id)] }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            16 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Buttons = [s.Buttons[0], s.Buttons[0]] }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            17 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Nodes = s.Nodes.SetItem(1, s.Nodes[1] with { Id = valid.Scripts[0].Id }) }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            18 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Nodes = s.Nodes.SetItem(1, s.Nodes[1] with { Bounds = new(0, 0, double.NaN, 3) }) }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            19 => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s, s]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()},
            _ => valid with { Scenes = ((System.Collections.Immutable.ImmutableArray<NovellMaker.Core.Screens.ScreenAsset>)[s with { Nodes = s.Nodes.SetItem(1, s.Nodes[1] with { ParentId = Guid.NewGuid() }) }]).Select(s => s is null ? null! : s.ToScene()).ToImmutableArray()}
        };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, valid); var original = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(path, invalid));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task StrictScreenSchemaRejectsMissingNullUnknownAndDuplicateFields()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, Fixture()); var json = File.ReadAllText(path);
            foreach (var property in new[] { "inputBindings", "scenes" })
            {
                var doc = JsonNode.Parse(json)!.AsObject(); doc.Remove(property); File.WriteAllText(path, doc.ToJsonString());
                await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            }
            foreach (var property in new[] { "nodes", "references", "buttons", "handlerScriptId", "pauseOnOverlay", "blockInputOnOverlay" })
            {
                var doc = JsonNode.Parse(json)!; doc["scenes"]![0]!["canvas"]!.AsObject().Remove(property); File.WriteAllText(path, doc.ToJsonString());
                await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            }
            foreach (var property in new[] { "text", "visible", "enabled", "parentId" })
            {
                var doc = JsonNode.Parse(json)!; doc["scenes"]![0]!["canvas"]!["nodes"]![1]!.AsObject().Remove(property); File.WriteAllText(path, doc.ToJsonString());
                await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            }
            File.WriteAllText(path, json.Replace("\"handlerScriptId\":" , "\"extra\": 1, \"handlerScriptId\":"));
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            File.WriteAllText(path, json.Replace("\"buttons\":", "\"buttons\": [], \"buttons\":"));
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void VisibleRegionsUseParentClippingVisibilityAndSubtreeOrder()
    {
        var root = Guid.NewGuid(); var panel = Guid.NewGuid(); var child = Guid.NewGuid(); var last = Guid.NewGuid();
        var definition = new ScreenDefinition(Guid.NewGuid(), [
            new(root, null, ScreenNodeKind.Panel, new(10, 10, 100, 100)),
            new(panel, root, ScreenNodeKind.Panel, new(80, 0, 50, 50)),
            new(last, root, ScreenNodeKind.Text, new(0, 0, 30, 30)),
            new(child, panel, ScreenNodeKind.Text, new(-10, 0, 60, 20))]);
        var regions = definition.VisibleRegions(new(0, 0, 100, 100)).ToArray();
        CollectionAssert.AreEqual(new[] { root, panel, child, last }, regions.Select(r => r.Node.Id).ToArray());
        Assert.AreEqual(new ScreenRect(90, 10, 10, 20), regions[2].Bounds);
        Assert.IsEmpty(definition.VisibleRegions(new(500, 500, 30, 30)).ToArray());
        var hidden = new ScreenDefinition(definition.Id, definition.Nodes.SetItem(1, definition.Nodes[1] with { Visible = false }));
        CollectionAssert.AreEqual(new[] { root, last }, hidden.VisibleRegions(new(0, 0, 100, 100)).Select(r => r.Node.Id).ToArray());
    }
}
