using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Tests.Execution;

[TestClass]
public sealed class ScriptAssetTests
{
    [TestMethod]
    public void SceneStartRunsResourceWithItsOwnReferencesAndRenameKeepsBinding()
    {
        var (project, scene, script) = Fixture("game.jump_line(refs.scene, refs.line)");
        script = script with { Name = "переименован.lua", References = Refs(("scene", scene.Id), ("line", scene.Lines[1].Id)) };
        project = project with { Scripts = [script] };
        ScenarioProgram.Validate(project);
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.AreEqual(scene.Lines[1].Id, player.State.LineId);
    }

    [TestMethod]
    public void DeferredCallReadsStateAfterWaitAndHasIndependentAliases()
    {
        var (project, scene, script) = Fixture("game.set('value', 'before')\ngame.wait(1)\ngame.set('value', 'after')\ngame.run(refs.child)\ngame.set('tail', 'done')");
        var child = new LuaScriptAsset(Guid.NewGuid(), "child.lua", "game.set('seen', game.get('value'))\ngame.set('own', refs.self)");
        child = child with { References = Refs(("self", child.Id)) };
        script = script with { References = Refs(("child", child.Id)) };
        project = project with { Scripts = [script, child] };
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        Assert.AreEqual("before", player.State.Variables["value"]);
        Assert.IsFalse(player.State.Variables.ContainsKey("seen"));
        var save = GameSave.Serialize(player);
        player.TickSettled(TimeSpan.FromSeconds(1));
        Assert.AreEqual("after", player.State.Variables["seen"]);
        Assert.AreEqual(child.Id.ToString(), player.State.Variables["own"]);
        Assert.AreEqual("done", player.State.Variables["tail"]);
        GameSave.Load(player, save);
        Assert.IsFalse(player.State.Variables.ContainsKey("seen"), "Loading must not execute a pending script.");
        player.TickSettled(TimeSpan.FromSeconds(1));
        Assert.AreEqual("after", player.State.Variables["seen"]);
    }

    [TestMethod]
    public void InlineLuaCanCallResourceAndJumpDiscardsRemainingCalls()
    {
        var (project, scene, script) = Fixture("game.set('unexpected', 'yes')");
        scene = scene with { ScriptBlocks = [new(null, ScriptPhase.SceneStart,
            new ScriptAction(Guid.NewGuid(), "game.jump_line(refs.scene, refs.line)\ngame.run(refs.script)", "inline.lua")
            { References = Refs(("scene", scene.Id), ("line", scene.Lines[1].Id), ("script", script.Id)) })] };
        project = project with { Scenes = [scene] };
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        Assert.IsNull(player.State.Error, player.State.Error);
        Assert.AreEqual(scene.Lines[1].Id, player.State.LineId);
        Assert.IsFalse(player.State.Variables.ContainsKey("unexpected"));
    }

    [TestMethod]
    public void RecursiveCallsAreBoundedWithoutRecursingOnHostStack()
    {
        var (project, scene, script) = Fixture("game.run(refs.self)");
        script = script with { References = Refs(("self", script.Id)) };
        var player = new ScenarioPlayer(project with { Scripts = [script] }, scene.Id, "ru").Settled();
        Assert.IsNotNull(player.State.Error);
        StringAssert.Contains(player.State.Error, "лимит действий");
    }

    [TestMethod]
    public void ExpandingCallQueueHasLimitEvenWhenScriptsContainWaits()
    {
        var (project, scene, script) = Fixture("game.wait(0.01)\nlocal i=0 while i<250 do game.run(refs.self) i=i+1 end");
        script = script with { References = Refs(("self", script.Id)) };
        var player = new ScenarioPlayer(project with { Scripts = [script] }, scene.Id, "ru").Settled();
        // Pending Lua is a host boundary: resume it before supplying the next virtual tick.
        for (var i = 0; i < 30 && player.State.Error is null; i++) player.TickSettled(TimeSpan.FromSeconds(0.01));
        Assert.IsNotNull(player.State.Error);
        StringAssert.Contains(player.State.Error, "лимит очереди Lua");
    }

    [TestMethod]
    public void FailingResourceDiscardsItsBatchAndReportsSourceAndLine()
    {
        var (project, scene, _) = Fixture("game.set('partial', 'bad')\nmissing_function()");
        var player = new ScenarioPlayer(project, scene.Id, "ru").Settled();
        Assert.IsNotNull(player.State.Error);
        StringAssert.Contains(player.State.Error, "chapter.lua");
        Assert.IsFalse(player.State.Variables.ContainsKey("partial"));
    }

    [TestMethod]
    public void SyntaxValidationDoesNotExecuteScript()
    {
        LuaActionScript.ValidateSyntax("while true do end", "loop.lua");
        Assert.Throws<ArgumentException>(() => LuaActionScript.ValidateSyntax("if then", "bad.lua"));
    }

    [TestMethod]
    public async Task ResourcesRoundTripAndAffectRevisionIncludingReferences()
    {
        var (project, _, script) = Fixture("game.set('flag', 'yes')");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var loaded = await store.LoadAsync(path);
            Assert.IsTrue(project.ContentEquals(loaded));
            foreach (var edited in new[] { script with { Source = "game.wait(1)" }, script with { References = Refs(("self", script.Id)) } })
            {
                Assert.IsFalse(project.ContentEquals(project with { Scripts = [edited] }));
                Assert.AreNotEqual(ScenarioProgram.Fingerprint(project), ScenarioProgram.Fingerprint(project with { Scripts = [edited] }));
            }
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task Version9MigratesWithoutWritingAndRejectsNewCommandsAndFields()
    {
        var (project, scene, _) = Fixture("");
        project = project with { Scripts = [], Scenes = [scene with { ScriptBlocks = [] }] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project);
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["formatVersion"] = 9;
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
            json.Remove("scripts"); await File.WriteAllTextAsync(path, json.ToJsonString());
            var bytes = File.ReadAllBytes(path);
            var migrated = await store.LoadAsync(path);
            Assert.AreEqual(NovelProject.CurrentFormatVersion, migrated.FormatVersion); Assert.IsTrue(migrated.Scripts.IsEmpty);
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            json["scenes"]![0]!["scriptBlocks"] = JsonNode.Parse($$$"""[{"lineId":null,"phase":"SceneStart","action":{"kind":"runScript","id":"{{{Guid.NewGuid()}}}","scriptId":"{{{Guid.NewGuid()}}}"}}]""");
            await File.WriteAllTextAsync(path, json.ToJsonString());
            await Assert.ThrowsExactlyAsync<ProjectFormatException>(() => store.LoadAsync(path));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task InvalidResourcesAndMissingCallsAreRejectedBeforePersistence()
    {
        var (project, _, script) = Fixture("");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        var store = new JsonProjectStore();
        foreach (var invalid in new[] { project with { Scripts = [] }, project with { Scripts = [script, script] },
            project with { Scripts = [script with { Id = project.Id }] }, project with { Scripts = [script with { Source = new string('x', LuaActionScript.MaximumSourceLength + 1) }] },
            project with { Scripts = [script with { References = Refs(("bad alias", script.Id)) }] } })
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(path, invalid));
        Assert.IsFalse(File.Exists(path));
    }

    private static ImmutableDictionary<string, Guid> Refs(params (string Name, Guid Id)[] refs) => refs.ToImmutableDictionary(r => r.Name, r => r.Id);
    private static (NovelProject Project, NovelScene Scene, LuaScriptAsset Script) Fixture(string source)
    {
        var script = new LuaScriptAsset(Guid.NewGuid(), "chapter.lua", source);
        var scene = NovelScene.Create("Глава") with { Lines = [DialogueLine.Create("ru", "Первая"), DialogueLine.Create("ru", "Вторая")],
            ScriptBlocks = [new(null, ScriptPhase.SceneStart, new RunScriptAction(Guid.NewGuid(), script.Id))] };
        return (NovelProject.Create("Скрипты") with { Scenes = [scene], Scripts = [script] }, scene, script);
    }
}
