using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class HistoryLuaTests
{
    [TestMethod]
    public void ReaddingDeletedTemplateKeepsItsEditedExternalHandlerWithoutDuplicateIds()
    {
        var project = ReadingScreenTemplates.Ensure(NovelProject.Create("Templates"));
        var scene = project.Scenes.Single(s => s.Canvas.References.ContainsKey("body"));
        var script = project.Scripts.Single(s => s.Id == scene.Canvas.HandlerScriptId);
        var edited = script with { Source = script.Source + "\n-- Author's change" };
        project = project with { Scenes = project.Scenes.Remove(scene), Scripts = project.Scripts.Replace(script, edited) };
        var restored = ReadingScreenTemplates.Ensure(project);
        ScreenAsset.ValidateProject(restored);
        Assert.AreEqual(2, restored.Scripts.Length); Assert.AreEqual(edited, restored.Scripts.Single(s => s.Id == script.Id));
    }

    [TestMethod]
    [DataRow("ui.get_history()")]
    [DataRow("ui.get_history(2,3,400)")]
    [DataRow("ui.replay_history_voice('11111111111111111111111111111111')")]
    public void HistoryCommandsAreValidatedBeforeHostAdmission(string expression)
    {
        var vm = new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "return {on_load=function() " + expression + " end,on_event=function(e) end}", ImmutableDictionary<string, Guid>.Empty);
        var batch = ScreenLuaProtocol.Prepare(vm.Definition, vm.Handle(null, ImmutableDictionary<string, string>.Empty));
        var command = batch.Service!; command.Validate();
        Assert.AreEqual(expression.Contains("replay", StringComparison.Ordinal) ? GameServiceAction.ReplayHistoryVoice : GameServiceAction.GetHistory, command.Action);
        Assert.Throws<ArgumentException>(() => (command with { Action = GameServiceAction.ListSlots }).Validate());
    }

    [TestMethod]
    [DataRow("ui.get_history(-1)")] [DataRow("ui.get_history(501)")] [DataRow("ui.get_history(1.5)")]
    [DataRow("ui.get_history('1')")] [DataRow("ui.get_history(nil)")] [DataRow("ui.get_history(0,0)")]
    [DataRow("ui.get_history(0,1,2049)")] [DataRow("ui.get_history(0,1,0)")] [DataRow("ui.get_history(0/0)")]
    [DataRow("ui.get_history(0,1,700,1)")] [DataRow("ui.replay_history_voice('')")]
    [DataRow("ui.replay_history_voice(1)")] [DataRow("ui.replay_history_voice('00000000000000000000000000000000')")]
    public void MalformedHistoryArgumentsCannotReachHost(string expression)
    {
        var vm = new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "return {on_event=function(e) " + expression + " end}", ImmutableDictionary<string, Guid>.Empty);
        Assert.Throws<Exception>(() => vm.Handle(new("update"), ImmutableDictionary<string, string>.Empty));
    }

    [TestMethod]
    public void HistoryProjectionPreservesLanguageVisitsUnicodeAndEveryPageWithoutExecutingStory()
    {
        var source = string.Concat(Enumerable.Repeat("日本語😀\n", 30));
        var line = DialogueLine.Create("en", "fallback") with { Texts = ImmutableDictionary<string, string>.Empty.Add("en", "fallback").Add("ja", source) };
        var scene = NovelScene.Create("Story") with { Lines = [line] };
        var project = NovelProject.Create("History") with { Scenes = [scene], DefaultLanguage = "en" };
        using var player = new ScenePlayer(project, scene.Id, "ja", mode: PlaybackMode.Game);
        var before = player.Execution.State;
        var first = HistoryPage.Read(project, before, 0, 1, 7, (_, language) => language == "ja"); first.Validate();
        var all = string.Concat(Enumerable.Range(1, first.Pages).Select(page => HistoryPage.Read(project, before, 1, page, 7, (_, _) => true).Text));
        Assert.AreEqual(source, all); Assert.AreEqual("ja", first.Language); Assert.IsTrue(first.CanReplay);
        Assert.AreEqual(before.History[0].VisitId, first.VisitId); Assert.AreSame(before, player.Execution.State);
        var newline = HistoryPage.Read(project, before, 0, 1, 2048, (_, _) => false);
        Assert.AreEqual(8, newline.Text.Count(c => c == '\n')); Assert.IsGreaterThan(1, newline.Pages);
        var empty = HistoryPage.Read(project, before with { History = [] }, 0, 1, 700, (_, _) => true);
        empty.Validate(); Assert.AreEqual(0, empty.Count); Assert.IsNull(empty.VisitId);
        Assert.Throws<ArgumentException>(() => (empty with { Text = "hidden" }).Validate());
    }

    [TestMethod]
    public async Task RealWorkerProjectsHistoryAndTemplateUsesOnlyAuthorLabels()
    {
        var story = NovelScene.Create("Story") with { Lines = [DialogueLine.Create("en", "A recorded line.")] };
        var project = ReadingScreenTemplates.Ensure(NovelProject.Create("Templates") with { Scenes = [story],
            InputBindings = [new("H", ScreenButtonAction.History, null)] });
        var scene = project.Scenes.Single(s => s.Id == project.InputBindings.Single().TargetSceneId);
        var stack = new ScreenStack(Guid.NewGuid()); var instance = stack.Open(Guid.NewGuid(), scene.AsScreen().Definition());
        await using var scripts = new ScreenLuaSession(stack);
        scripts.Attach(instance, project.Scripts.Single(s => s.Id == scene.Canvas.HandlerScriptId).Source, scene.Canvas.References);
        await ScreenLuaSessionTests.Drain(scripts);
        var command = scripts.DrainServices().Single().Command;
        using var player = new ScenePlayer(project, story.Id, "en", mode: PlaybackMode.Game);
        var history = HistoryPage.Read(project, player.Execution.State, 0, 1, 700, (_, _) => true);
        var result = new GameServiceResult(command.RequestId, command.Action, true, "ok", null, []) { History = history };
        Assert.AreEqual(ScreenEventAdmission.Accepted, scripts.Enqueue(instance, new("service_result") { Result = result }, ImmutableDictionary<string, string>.Empty));
        await ScreenLuaSessionTests.Drain(scripts); Assert.IsEmpty(scripts.Diagnostics);
        Assert.AreEqual("A recorded line.", instance.Definition.Nodes.Single(n => n.Name == "body").Text);
        Assert.IsTrue(instance.Definition.Nodes.Single(n => n.Name == "replay").Enabled);
        Assert.Throws<ArgumentException>(() => (result with { Action = GameServiceAction.GetSettings }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { History = history with { Index = 2 } }).Validate());
    }

    [TestMethod]
    public async Task LegacyReadingWindowsMigrateDeterministicallyAndAuthorChangesRoundTrip()
    {
        var scene = NovelScene.Create("Story");
        var project = ReadingScreenTemplates.Ensure(NovelProject.Create("Migration") with { Scenes = [scene],
            InputBindings = [new("H", ScreenButtonAction.History, null), new("R", ScreenButtonAction.ReadingSettings, null)] });
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project.novell"); var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            json["formatVersion"] = 22; json["scripts"] = new JsonArray();
            json["scenes"] = new JsonArray(json["scenes"]![0]!.DeepClone());
            foreach (var binding in json["inputBindings"]!.AsArray()) binding!["targetSceneId"] = null;
            var legacy = json.ToJsonString(); await File.WriteAllTextAsync(path, legacy);
            var migrated = await store.LoadAsync(path); var repeated = await store.LoadAsync(path);
            Assert.AreEqual(23, migrated.FormatVersion); Assert.IsTrue(migrated.ContentEquals(repeated));
            Assert.AreEqual(legacy, await File.ReadAllTextAsync(path));
            var historyScene = migrated.Scenes.Single(s => s.Id == migrated.InputBindings[0].TargetSceneId);
            var title = historyScene.Canvas.Nodes.Single(n => n.Name == "title");
            migrated = migrated.WithCanvas(historyScene.AsScreen() with { Nodes = historyScene.Canvas.Nodes.Replace(title, title with { Text = "Backlog", Bounds = new(20, 40, 900, 50), Color = "#12ABCD" }) });
            await store.SaveAsync(path, migrated);
            Assert.IsTrue(migrated.ContentEquals(await store.LoadAsync(path)));
            Assert.IsTrue(migrated.ContentEquals(ReadingScreenTemplates.Ensure(migrated)));
            var broken = JsonNode.Parse(await File.ReadAllTextAsync(path))!; broken["inputBindings"]![0]!["targetSceneId"] = null;
            await File.WriteAllTextAsync(path, broken.ToJsonString());
            await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}
