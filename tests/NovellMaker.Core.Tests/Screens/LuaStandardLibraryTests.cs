using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class LuaStandardLibraryTests
{
    private static readonly ImmutableDictionary<string, Guid> Empty = ImmutableDictionary<string, Guid>.Empty;
    private static (Script Script, LuaStandardLibrary Library) Vm()
    {
        var script = new Script(CoreModules.None); var library = new LuaStandardLibrary(script);
        library.Install(script.Globals); return (script, library);
    }
    private static DynValue Evaluate(string code) => Vm().Script.DoString(code);

    [TestMethod]
    [DataRow("math.abs(-3)", 3d)] [DataRow("math.ceil(1.2)", 2d)] [DataRow("math.floor(-1.2)", -2d)]
    [DataRow("math.sqrt(9)", 3d)] [DataRow("math.sin(0)", 0d)] [DataRow("math.cos(0)", 1d)]
    [DataRow("math.tan(0)", 0d)] [DataRow("math.asin(0)", 0d)] [DataRow("math.acos(1)", 0d)]
    [DataRow("math.atan(0)", 0d)] [DataRow("math.atan2(0,1)", 0d)] [DataRow("math.exp(0)", 1d)]
    [DataRow("math.deg(math.pi)", 180d)] [DataRow("math.rad(180)", Math.PI)]
    [DataRow("math.pow(2,3)", 8d)] [DataRow("math.fmod(-7,3)", -1d)]
    [DataRow("math.log(1)", 0d)] [DataRow("math.log(8,2)", 3d)]
    [DataRow("math.min(4,-1,2)", -1d)] [DataRow("math.max(4,-1,2)", 4d)]
    [DataRow("tonumber(' 1.25e2 ')", 125d)] [DataRow("tonumber(12)", 12d)]
    public void FiniteMathAndDecimalConversion(string expression, double expected)
        => Assert.AreEqual(expected, Evaluate("return " + expression).Number, 1e-12);

    [TestMethod]
    [DataRow("return string.sub('abcdef',2,-2)", "bcde")]
    [DataRow("return string.sub('abcdef',-3)", "def")]
    [DataRow("return string.sub('abc',-2147483648,2147483647)", "abc")]
    [DataRow("return string.sub('abc',2147483647,-2147483648)", "")]
    [DataRow("return string.lower('ПРИВЕТ Lua')", "привет lua")]
    [DataRow("return string.upper('привет Lua')", "ПРИВЕТ LUA")]
    [DataRow("return string.reverse('абв')", "вба")]
    [DataRow("return string.rep('ab',3,':')", "ab:ab:ab")]
    [DataRow("return string.rep('a',0)", "")]
    [DataRow("return table.concat({'a',2,'c'},'-',2,3)", "2-c")]
    [DataRow("return table.concat({})", "")]
    [DataRow("return table.concat({'a'},'',2,1)", "")]
    [DataRow("local t={'b'}; table.insert(t,1,'a'); table.insert(t,'c'); table.remove(t,2); return table.concat(t)", "ac")]
    [DataRow("return tostring(nil)..'/'..tostring(true)..'/'..tostring(false)..'/'..tostring(1.25)..'/'..tostring('ok')", "nil/true/false/1.25/ok")]
    [DataRow("return type(nil)..type(true)..type(1)..type('x')..type({})..type(function() end)..type(math.abs)", "nilbooleannumberstringtablefunctionfunction")]
    public void StringsListsAndScalars(string code, string expected) => Assert.AreEqual(expected, Evaluate(code).String);

    [TestMethod]
    [DataRow("local a,b=math.modf(-1.25); return a==-1 and b==-0.25")]
    [DataRow("return string.len('абв')==3")]
    [DataRow("local a,b=string.find('a.b.c','.',2,true); return a==2 and b==2")]
    [DataRow("local a,b=string.find('a.b.c','.',-2); return a==4 and b==4")]
    [DataRow("local a,b=string.find('abc','',4); return a==4 and b==3")]
    [DataRow("return string.find('abc','z')==nil and string.find('abc','',5)==nil")]
    [DataRow("return tonumber('no')==nil and tonumber('nan')==nil and tonumber('Infinity')==nil and tonumber('1e999')==nil and tonumber({})==nil")]
    [DataRow("return table.remove({})==nil and table.remove({'x'})=='x'")]
    [DataRow("local s=''; for i,v in ipairs({'a','b'}) do s=s..i..v end; return s=='1a2b'")]
    [DataRow("local n=0; for k,v in pairs({a=2,b=3}) do n=n+v end; return n==5")]
    [DataRow("local t={'a','b'}; local it=ipairs(t); t[1]='x'; local i,v=it(); return i==1 and v=='a'")]
    [DataRow("local t={a='a'}; local it=pairs(t); t.a='x'; local k,v=it(); return k=='a' and v=='a'")]
    [DataRow("local t={'a','b'}; t[2]=nil; return table.concat(t)=='a'")]
    public void TuplesIteratorsAndEmptyResults(string code) => Assert.IsTrue(Evaluate(code).Boolean);

    [TestMethod]
    [DataRow("math.sqrt(-1)")] [DataRow("math.exp(1000)")] [DataRow("math.pow(10,1000)")]
    [DataRow("math.log(0)")] [DataRow("math.log(2,1)")] [DataRow("math.fmod(1,0)")]
    [DataRow("math.abs(0/0)")] [DataRow("math.abs(1/0)")] [DataRow("math.abs('2')")]
    [DataRow("math.min()")][DataRow("math.max(1,'a')")][DataRow("math.floor(1,2)")]
    [DataRow("string.len(1)")] [DataRow("string.sub('a',1.5)")] [DataRow("string.sub('a',2147483648)")]
    [DataRow("string.rep('a',-1)")] [DataRow("string.rep('',8193)")] [DataRow("string.rep('aa',4097)")]
    [DataRow("string.rep('a',4097,':')")] [DataRow("string.find('a','.',1,false)")] [DataRow("string.find('a','.',1,1)")]
    [DataRow("string.find('a','.',nil)")] [DataRow("table.concat({true})")] [DataRow("table.concat({1/0})")]
    [DataRow("table.concat({a='a'})")] [DataRow("table.concat({[2]='b'})")]
    [DataRow("table.concat({'a'},'',0)")] [DataRow("table.concat({'a'},'',1,2)")]
    [DataRow("table.concat({'a'},'',1,-1)")] [DataRow("table.concat({'a'},'',3)")]
    [DataRow("table.insert({},nil)")] [DataRow("table.insert({},0,'x')")]
    [DataRow("table.remove({},1)")] [DataRow("table.remove({'a'},2)")]
    [DataRow("tostring({})")] [DataRow("tostring(function() end)")] [DataRow("tostring(1/0)")]
    [DataRow("tonumber(0/0)")] [DataRow("pairs(1)")] [DataRow("ipairs({[2]='x'})")]
    public void InvalidOrExpensiveCallsAreRejected(string expression)
        => Assert.Throws<InterpreterException>(() => Evaluate("return " + expression));

    [TestMethod]
    [DataRow("os")] [DataRow("io")] [DataRow("debug")] [DataRow("package")] [DataRow("require")]
    [DataRow("load")] [DataRow("loadstring")] [DataRow("loadfile")] [DataRow("dofile")]
    [DataRow("collectgarbage")] [DataRow("luanet")] [DataRow("clr")] [DataRow("coroutine")]
    [DataRow("getmetatable")] [DataRow("setmetatable")] [DataRow("rawget")] [DataRow("rawset")]
    [DataRow("math.random")] [DataRow("math.randomseed")] [DataRow("math.huge")]
    [DataRow("string.dump")] [DataRow("string.format")] [DataRow("string.gsub")]
    [DataRow("string.match")] [DataRow("string.gmatch")] [DataRow("table.sort")] [DataRow("_G")]
    public void EverythingOutsideTheAllowlistRemainsUnavailable(string expression)
        => Assert.IsTrue(Evaluate("return " + expression).IsNil());

    [TestMethod]
    public void ExactTextResultAndAggregateBoundariesAreCheckedBeforeAllocation()
    {
        var (script, library) = Vm();
        script.Globals.Set("limit", DynValue.NewString(new string('x', 8192)));
        script.Globals.Set("large", DynValue.NewString(new string('x', 8193)));
        Assert.AreEqual(8192, script.DoString("return string.rep('x',8192)").String.Length);
        library.ResetBudget();
        Assert.AreEqual(8192, script.DoString("return string.len(string.sub(limit,1))").Number);
        foreach (var expression in new[] { "string.len(large)", "tostring(large)", "tonumber(large)", "table.concat({limit,'x'})" })
        { library.ResetBudget(); Assert.Throws<InterpreterException>(() => script.DoString("return " + expression)); }
        library.ResetBudget();
        script.DoString("for i=1,4 do string.sub(limit,1) end");
        Assert.Throws<InterpreterException>(() => script.DoString("return string.sub('x',1)"));
        library.ResetBudget(); Assert.AreEqual("x", script.DoString("return string.sub('x',1)").String);
    }

    [TestMethod]
    public void TableAndSearchWorkBoundariesDoNotDependOnLuaInstructionCounter()
    {
        var (script, library) = Vm(); var list = new Table(script);
        for (var i = 1; i <= 1024; i++) list.Set(i, DynValue.NewString("x"));
        script.Globals.Set("items", DynValue.NewTable(list));
        Assert.AreEqual(1024, script.DoString("return string.len(table.concat(items))").Number);
        Assert.Throws<InterpreterException>(() => script.DoString("table.insert(items,'extra')"));
        list.Set(1025, DynValue.True);
        Assert.Throws<InterpreterException>(() => script.DoString("return pairs(items)"));
        var metatable = new Table(script); var value = new Table(script) { MetaTable = metatable };
        script.Globals.Set("meta", DynValue.NewTable(value));
        Assert.Throws<InterpreterException>(() => script.DoString("return ipairs(meta)"));
        library.ResetBudget();
        script.Globals.Set("haystack", DynValue.NewString(new string('a', 256)));
        script.Globals.Set("needle", DynValue.NewString(new string('b', 256)));
        // One unit for the call plus 256*256 character comparisons exceeds 65536.
        Assert.Throws<InterpreterException>(() => script.DoString("return string.find(haystack,needle)"));
        library.ResetBudget(); script.Globals.Set("needle", DynValue.NewString(new string('b', 255)));
        Assert.IsTrue(script.DoString("return string.find(haystack,needle)").IsNil());
        library.ResetBudget();
        for (var i = 0; i < LuaStandardLibrary.MaximumWork; i++) script.Call(script.Globals.Get("math").Table.Get("abs"), DynValue.NewNumber(-1));
        Assert.Throws<InterpreterException>(() => script.DoString("return math.abs(1)"));
        library.ResetBudget(); Assert.AreEqual(1, script.DoString("return math.abs(1)").Number);
    }

    [TestMethod]
    public void IteratorsAndListMutationsChargeWorkBeforeChangingData()
    {
        var (script, library) = Vm();
        script.DoString("items={1,2}; iterator=ipairs(items)");
        for (var i = 0; i < LuaStandardLibrary.MaximumWork - 3; i++) script.Call(script.Globals.Get("iterator"));
        Assert.Throws<InterpreterException>(() => script.Call(script.Globals.Get("iterator")));
        library.ResetBudget();
        for (var i = 0; i < LuaStandardLibrary.MaximumWork - 4; i++) script.Call(script.Globals.Get("math").Table.Get("abs"), DynValue.NewNumber(0));
        Assert.Throws<InterpreterException>(() => script.DoString("table.insert(items,1,3)"));
        Assert.AreEqual(1, script.Globals.Get("items").Table.Get(1).Number); Assert.AreEqual(2, script.Globals.Get("items").Table.Get(2).Number);
    }

    [TestMethod]
    public void ArgumentLimitsAndOpaqueValuesDoNotOpenImplicitConversions()
    {
        var (script, _) = Vm(); var min = script.Globals.Get("math").Table.Get("min");
        var arguments = Enumerable.Repeat(DynValue.NewNumber(2), 64).ToArray();
        Assert.AreEqual(2, script.Call(min, arguments).Number);
        Assert.Throws<InterpreterException>(() => script.Call(min, [.. arguments, DynValue.NewNumber(1)]));
        script.Globals.Set("marker", new LuaValueProjection(script).Null);
        Assert.AreEqual("userdata", script.DoString("return type(marker)").String);
        Assert.Throws<InterpreterException>(() => script.DoString("return tostring(marker)"));
        Assert.Throws<InterpreterException>(() => script.DoString("return tonumber('10',16)"));
    }

    [TestMethod]
    public async Task RealWorkerImportsShareTheLoadBudgetAsWellAsTheEventBudget()
    {
        var root = Guid.NewGuid(); var child = Guid.NewGuid();
        const string source = "for i=1,3 do string.rep('x',8192) end; local m=ui.import(modules.child); return {on_event=function() end}";
        var library = new LuaModuleLibrary(root, [new(root, "root", source) { References = Empty.Add("child", child) },
            new(child, "child", "string.rep('x',8192); return {}")]);
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        var before = instance.Definition;
        session.Attach(instance, source, modules: library); await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); StringAssert.Contains(session.Diagnostics[0], "бюджет"); Assert.AreSame(before, instance.Definition);
        var other = stack.Open(instance.OwnerId, before);
        session.Attach(other, "math.abs=nil; return {on_event=function() end}"); await ScreenLuaSessionTests.Drain(session);
        var fresh = stack.Open(instance.OwnerId, before);
        session.Attach(fresh, "return {on_load=function() ui.set_text(refs.label,tostring(math.abs(-2))) end,on_event=function() end}", Empty.Add("label", before.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); Assert.AreEqual("2", fresh.Definition.Nodes[2].Text);
        Assert.AreEqual(1, session.Diagnostics.Length);
    }

    [TestMethod]
    public async Task RealWorkerModulesHaveIndependentTablesAndShareTheInvocationBudget()
    {
        var root = Guid.NewGuid(); var child = Guid.NewGuid();
        const string source = """
            math.floor=function() return -1 end
            local m=ui.import(modules.child)
            return {on_load=function() ui.set_state(1); ui.set_text(refs.label,m.caption()) end,
              on_event=function() ui.set_state(2); ui.set_text(refs.label,'bad');
                for i=1,3 do string.rep('a',8192) end; m.expensive()
              end}
            """;
        var library = new LuaModuleLibrary(root, [new(root, "root", source) { References = Empty.Add("child", child) },
            new(child, "child", "return {caption=function() return tostring(math.floor(2.5)) end,expensive=function() for i=1,2 do string.rep('a',8192) end end}")]);
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, Empty.Add("label", instance.Definition.Nodes[2].Id), modules: library);
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("2", instance.Definition.Nodes[2].Text);
        var state = session.GetState(instance); var definition = instance.Definition;
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty);
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); StringAssert.Contains(session.Diagnostics[0], "бюджет");
        Assert.AreSame(state, session.GetState(instance)); Assert.AreSame(definition, instance.Definition);
    }

    [TestMethod]
    [DataRow("string.rep('x',8193)")] [DataRow("string.find(string.rep('a',256),string.rep('b',256))")]
    [DataRow("math.sqrt(-1)")] [DataRow("os.execute('no')")] [DataRow("require('file')")]
    public async Task RealWorkerFailureDoesNotCommitStateVisualsOrServices(string expression)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() ui.set_state(1) end,on_event=function() ui.set_state(2); ui.set_text(refs.label,'bad'); ui.save_slot('slot'); " + expression + " end}",
            Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        var state = session.GetState(instance); var definition = instance.Definition;
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(state, session.GetState(instance)); Assert.AreSame(definition, instance.Definition);
        Assert.IsEmpty(session.DrainServices());
    }

    [TestMethod]
    public async Task RealWorkerBudgetResetsForEachEventAndInstance()
    {
        const string source = "return {on_load=function() ui.set_text(refs.label,string.sub(string.rep('a',8192),1,1)) end,on_event=function() ui.set_text(refs.label,string.sub(string.rep('b',8192),1,1)) end}";
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, source, Empty.Add("label", instance.Definition.Nodes[2].Id)); await ScreenLuaSessionTests.Drain(session);
        for (var i = 0; i < 5; i++) { session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty); await ScreenLuaSessionTests.Drain(session); }
        var other = stack.Open(instance.OwnerId, instance.Definition);
        session.Attach(other, source, Empty.Add("label", instance.Definition.Nodes[2].Id)); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("b", instance.Definition.Nodes[2].Text); Assert.AreEqual("a", other.Definition.Nodes[2].Text);
    }

    [TestMethod]
    public void ScenarioV1RetainsItsPreviousEnvironment()
    {
        var project = NovellMaker.Core.Projects.NovelProject.Create("v1");
        var scene = NovellMaker.Core.Scenes.NovelScene.Create("scene"); project = project with { Scenes = [scene] };
        var state = new GameState { ProjectId = project.Id, Revision = "test", SceneId = scene.Id };
        var commands = LuaActionScript.RunInWorker(new(Guid.NewGuid(), "game.set('result',math==nil and string==nil and table==nil and tostring==nil and 'ok' or 'bad')", "v1"),
            LuaStateSnapshot.Capture(state), LuaCommandCatalog.Capture(project, scene.Id));
        Assert.AreEqual("ok", ((SetAction)commands.Single()).Value);
    }
}
