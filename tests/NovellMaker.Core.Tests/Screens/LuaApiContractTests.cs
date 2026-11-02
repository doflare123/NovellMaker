using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Minigames;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Tests.Execution;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class LuaApiContractTests
{
    private static readonly ImmutableDictionary<string, string> Empty = ImmutableDictionary<string, string>.Empty;

    private static ImmutableArray<ScenarioAction> Run(string source, ImmutableDictionary<string, string>? variables = null,
        MinigameOutcome? outcome = null)
    {
        var scene = NovelScene.Create("Contract");
        var project = NovelProject.Create("Contract") with { Scenes = [scene] };
        var state = new GameState { ProjectId = project.Id, Revision = "contract", SceneId = scene.Id,
            Variables = variables ?? Empty, MinigameOutcome = outcome };
        return LuaActionScript.RunInWorker(new(Guid.NewGuid(), source, "contract.lua"),
            LuaStateSnapshot.Capture(state), LuaCommandCatalog.Capture(project, scene.Id));
    }

    private static ScreenLuaVm Vm(string body, string setup = "", MinigameInvocation? minigame = null)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        return new(definition, Guid.NewGuid(), setup + "\nreturn {on_load=function() " + body + " end,on_event=function() end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id), minigame: minigame);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("game.strict_arguments(true); game.strict_arguments(false);")]
    public void ScenarioCompatibilityPreservesCoercionsAndExtraArguments(string setup)
    {
        var commands = Run(setup + "game.wait('1','extra'); game.set(12,34,'extra'); game.time('extra'); game.finish(56,'extra')");
        Assert.AreEqual(1d, ((WaitAction)commands[0]).Seconds);
        Assert.AreEqual("12", ((SetAction)commands[1]).Variable);
        Assert.AreEqual("34", ((SetAction)commands[1]).Value);
        Assert.AreEqual("56", ((SetAction)commands[2]).Value);
    }

    [TestMethod]
    [DataRow("game.get()")]
    [DataRow("game.set('key','value','extra')")]
    [DataRow("game.time(nil)")]
    [DataRow("game.visible()")]
    [DataRow("game.object()")]
    [DataRow("game.show()")]
    [DataRow("game.hide()")]
    [DataRow("game.state('id')")]
    [DataRow("game.text()")]
    [DataRow("game.place('id',0,0,1)")]
    [DataRow("game.auto()")]
    [DataRow("game.move('id',0,0,1,0)")]
    [DataRow("game.wait()")]
    [DataRow("game.wait_input(nil)")]
    [DataRow("game.wait_actions(nil)")]
    [DataRow("game.wait_click(nil,nil)")]
    [DataRow("game.hotspot('id','label',0,0,1)")]
    [DataRow("game.finish()")]
    [DataRow("game.start_minigame('id',nil,nil)")]
    [DataRow("game.minigame_result(nil)")]
    [DataRow("game.run()")]
    [DataRow("game.jump('id')")]
    [DataRow("game.jump_line('id')")]
    [DataRow("game.choice({},nil)")]
    public void ScenarioStrictCountsAreCheckedBeforeCommandConstruction(string call)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Run("game.strict_arguments(true); " + call));
        StringAssert.Contains(error.Message, "количество аргументов");
        StringAssert.Contains(error.Message, call[..call.IndexOf('(')]);
    }

    [TestMethod]
    [DataRow("game.wait('1')")]
    [DataRow("game.wait(0/0)")]
    [DataRow("game.wait(1/0)")]
    [DataRow("game.set(12,'value')")]
    [DataRow("game.set('key',34)")]
    [DataRow("game.finish(56)")]
    [DataRow("game.get(nil)")]
    [DataRow("game.wait_click(nil)")]
    [DataRow("game.choice('table')")]
    public void ScenarioStrictTypesRejectCoercionsNilAndNonFiniteNumbers(string call)
        => Assert.Throws<InvalidOperationException>(() => Run("game.strict_arguments(true); " + call));

    [TestMethod]
    public void ScenarioStrictModeAcceptsValidCallsAndResetsForEveryInvocation()
    {
        var commands = Run("game.strict_arguments(true); game.wait(1); game.wait_click(); game.set('key','value'); game.finish('win'); game.set('seen',game.get('minigame.result')); game.time(); game.minigame_result()");
        Assert.IsNull(((WaitAction)commands[1]).Token);
        Assert.AreEqual("win", ((SetAction)commands[4]).Value);
        Assert.AreEqual(1d, ((WaitAction)Run("game.wait('1')").Single()).Seconds);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("nil")]
    [DataRow("1")]
    [DataRow("'true'")]
    [DataRow("true,nil")]
    public void ModeSwitchAlwaysRequiresExactlyOneBoolean(string args)
    {
        Assert.Throws<InvalidOperationException>(() => Run($"game.strict_arguments({args})"));
        Assert.Throws<ScriptRuntimeException>(() => Vm("", $"ui.strict_arguments({args})"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("ui.strict_arguments(true); ui.strict_arguments(false)")]
    public void UiCompatibilityPreservesCoercionsAndExtraArguments(string setup)
    {
        var vm = Vm("ui.set_text(refs.label,42,'extra'); ui.get('missing','extra'); ui.get_state('extra')", setup);
        Assert.AreEqual("42", vm.Handle(null, Empty).Single().Text);
    }

    [TestMethod]
    [DataRow("1", true)]
    [DataRow("0", true)]
    [DataRow("''", true)]
    [DataRow("{}", true)]
    [DataRow("false", false)]
    public void UiCompatibilityRetainsMoonSharpBooleanConversion(string value, bool expected)
    {
        var changes = Vm($"ui.set_visible(refs.label,{value}); ui.set_enabled(refs.label,{value})").Handle(null, Empty);
        Assert.IsTrue(changes.All(change => change.Value == expected));
    }

    [TestMethod]
    [DataRow("ui.set_text(refs.label,'text',nil)")]
    [DataRow("ui.set_visible(refs.label,true,nil)")]
    [DataRow("ui.set_enabled(refs.label,true,nil)")]
    [DataRow("ui.set_geometry(refs.label,{},nil)")]
    [DataRow("ui.set_parent(refs.label)")]
    [DataRow("ui.remove()")]
    [DataRow("ui.find()")]
    [DataRow("ui.create(refs.label)")]
    [DataRow("ui.close(nil)")]
    [DataRow("ui.get()")]
    [DataRow("ui.minigame_parameters(nil)")]
    [DataRow("ui.is_restoring(nil)")]
    [DataRow("ui.complete_minigame()")]
    [DataRow("ui.cancel_minigame(nil)")]
    [DataRow("ui.list()")]
    [DataRow("ui.object()")]
    [DataRow("ui.get_state(nil)")]
    [DataRow("ui.set_state()")]
    [DataRow("ui.trigger('id',nil,nil)")]
    [DataRow("ui.new_game(nil,nil)")]
    [DataRow("ui.main_menu(nil)")]
    [DataRow("ui.end_session(nil)")]
    [DataRow("ui.continue_game(nil)")]
    [DataRow("ui.list_slots(nil)")]
    [DataRow("ui.save_slot(nil,nil,nil)")]
    [DataRow("ui.load_slot()")]
    [DataRow("ui.delete_slot()")]
    [DataRow("ui.get_settings(nil)")]
    [DataRow("ui.check_game_release(nil)")]
    [DataRow("ui.cancel_release_check()")]
    [DataRow("ui.get_persistent()")]
    [DataRow("ui.set_persistent('key',nil,nil)")]
    [DataRow("ui.set_setting('text_speed')")]
    public void UiStrictCountsAreCheckedBeforeEffectsOrContext(string call)
    {
        var vm = Vm(call, "ui.strict_arguments(true)");
        var error = Assert.Throws<ScriptRuntimeException>(() => vm.Handle(null, Empty));
        StringAssert.Contains(error.Message, "количество аргументов");
        StringAssert.Contains(error.Message, call[..call.IndexOf('(')]);
    }

    [TestMethod]
    [DataRow("ui.set_text(refs.label,42)")]
    [DataRow("ui.set_visible(refs.label,1)")]
    [DataRow("ui.set_enabled(refs.label,0)")]
    [DataRow("ui.set_visible(refs.label,nil)")]
    [DataRow("ui.set_enabled(refs.label,'')")]
    [DataRow("ui.set_geometry(refs.label,'table')")]
    [DataRow("ui.find(1)")]
    [DataRow("ui.get(1)")]
    [DataRow("ui.save_slot(nil,42)")]
    public void UiStrictTypesRejectCoercions(string call)
        => Assert.Throws<ScriptRuntimeException>(() => Vm(call, "ui.strict_arguments(true)").Handle(null, Empty));

    [TestMethod]
    public void UiStrictModeAllowsDocumentedNilAndOptionalArguments()
    {
        var vm = Vm("""
            ui.set_state(nil)
            ui.complete_minigame(nil)
            ui.trigger('next',nil)
            ui.save_slot()
            ui.save_slot(nil,nil)
            ui.new_game(nil)
            ui.set_persistent('key',nil)
            ui.set_persistent('key')
            """, "ui.strict_arguments(true)", new(LuaValue.Null, false));
        var changes = vm.Handle(null, Empty);
        Assert.AreEqual(LuaValueKind.Null, changes[0].State!.Kind);
        Assert.AreEqual(LuaValueKind.Null, changes[1].MinigameResult!.Data.Kind);
        Assert.HasCount(8, changes);
    }

    [TestMethod]
    public void StrictModeIsSharedWithModulesIncludingImportArity()
    {
        var root = Guid.NewGuid(); var child = Guid.NewGuid(); var definition = ScreenLuaProtocolTests.Definition();
        var bundle = new LuaModuleBundle(root, ImmutableDictionary<string, Guid>.Empty.Add("child", child),
            [new(child, "ui.strict_arguments(true); return {set=function() ui.set_state(1,2) end}", ImmutableDictionary<string, Guid>.Empty)]);
        ScreenLuaVm Create(string source) => new(definition, Guid.NewGuid(), source, ImmutableDictionary<string, Guid>.Empty, bundle);
        // A module may enable the VM-wide policy while being imported at bootstrap.
        var vm = Create("local m=ui.import(modules.child); return {on_event=function() m.set() end}");
        Assert.Throws<ScriptRuntimeException>(() => vm.Handle(new("update"), Empty));
        Assert.Throws<ScriptRuntimeException>(() => Create("ui.strict_arguments(true); ui.import(modules.child,nil); return {on_event=function() end}"));
        var legacy = Create("ui.import(modules.child,nil); return {on_event=function() end}");
        Assert.IsEmpty(legacy.Handle(null, Empty));
    }

    [TestMethod]
    public async Task StrictModePersistsAcrossRealWorkerEventsAndFailureDoesNotCommitPartialChanges()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            ui.strict_arguments(true)
            return {on_load=function() ui.set_text(refs.label,'ready') end,
              on_event=function() ui.set_text(refs.label,'partial'); ui.set_state(1,2) end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual("ready", instance.Definition.Nodes[2].Text);
        session.Enqueue(instance, new("click", instance.Definition.Nodes[1].Id), Empty);
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics);
        Assert.AreEqual("ready", instance.Definition.Nodes[2].Text);
        Assert.AreEqual("42", Vm("ui.set_text(refs.label,42)").Handle(null, Empty).Single().Text);
    }

    [TestMethod]
    public void FinishUsesTheSameLocalVariablesAndLimitsAsSet()
    {
        var variables = Empty.Add("minigame.result", "old");
        var commands = Run("game.set('minigame.result','set'); game.finish('finish'); game.set('seen',game.get('minigame.result')); game.set('minigame.result','last')", variables);
        Assert.AreEqual("finish", ((SetAction)commands[2]).Value);
        Assert.AreEqual("old", variables["minigame.result"]);
        var full = Enumerable.Range(0, LuaLimits.MaximumVariableCount).ToImmutableDictionary(i => "v" + i, _ => "value");
        Assert.Throws<InvalidOperationException>(() => Run("game.finish('win')", full));
        Assert.Throws<InvalidOperationException>(() => Run("game.finish('" + new string('x', LuaLimits.MaximumValueLength + 1) + "')"));
        Assert.HasCount(1, Run("game.finish('win')", full.Remove("v0").Add("minigame.result", "old")));
    }

    [TestMethod]
    public void FinishFailureLeavesTheHostUnchanged()
    {
        var scene = NovelScene.Create("Contract") with { UseActions = true,
            Actions = [new ScriptAction(Guid.NewGuid(), "game.finish('partial'); game.strict_arguments(true); game.set(1,2)", "failure.lua")] };
        using var player = new ScenarioPlayer(NovelProject.Create("Contract") with { Scenes = [scene] }, scene.Id, "ru").Settled();
        Assert.IsNotNull(player.State.Error);
        Assert.IsFalse(player.State.Variables.ContainsKey("minigame.result"));
    }

    [TestMethod]
    public void ResultRetainsFalseZeroEmptyStringsNestedNullsAndDetachedTables()
    {
        foreach (var json in new[] { "false", "0", "\"\"" })
        {
            var commands = Run("local r=game.minigame_result(); game.set('present',r.data~=nil and 'yes' or 'no')", outcome:
                new(Guid.NewGuid(), new(MinigamePhase.Completed, LuaValue.FromJson(json), "")));
            Assert.AreEqual("yes", ((SetAction)commands.Single()).Value);
        }
        var outcome = new MinigameOutcome(Guid.NewGuid(), new(MinigamePhase.Completed, LuaValue.FromJson("{\"items\":[null,2]}"), ""));
        var result = Run("local r=game.minigame_result(); r.data.items[2]=3; local again=game.minigame_result(); game.set('intact',again.data.items[1]~=nil and again.data.items[2]==2 and 'yes' or 'no')", outcome: outcome);
        Assert.AreEqual("yes", ((SetAction)result.Single()).Value);
        Assert.AreEqual(2d, outcome.Result.Data.Properties!["items"].Items[1].Number);
        Assert.AreEqual("yes", ((SetAction)Run("game.set('absent',game.minigame_result()==nil and 'yes' or 'no')").Single()).Value);
    }
}
