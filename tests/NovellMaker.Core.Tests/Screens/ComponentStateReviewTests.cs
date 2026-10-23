using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Scripting;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class ComponentStateReviewTests
{
    private static ImmutableDictionary<string, string> Empty => ImmutableDictionary<string, string>.Empty;

    [TestMethod]
    [DataRow(false)] [DataRow(true)]
    public void NilAndVoidEntriesAreAbsentFromProjectedObjects(bool useVoid)
    {
        var script = new Script(CoreModules.None); var projection = new LuaValueProjection(script);
        var table = new Table(script); table.Set("deleted", DynValue.NewNumber(1));
        table.Set("deleted", useVoid ? DynValue.Void : DynValue.Nil);
        table.Set("kept", projection.Null);
        Assert.AreEqual("{\"kept\":null}", projection.Read(DynValue.NewTable(table)).ToJson());
    }

    [TestMethod]
    [DataRow("local s={a=1,b=2}; s.a=nil", "{\"b\":2}")]
    [DataRow("local s={a=1}; s.a=nil", "{}")]
    [DataRow("local s={1,2,3}; s[3]=nil", "[1,2]")]
    [DataRow("local s=ui.list({1}); s[1]=nil", "[]")]
    [DataRow("local s={[1]=1,b=2}; s[1]=nil", "{\"b\":2}")]
    [DataRow("local s={a=1,1,2}; s.a=nil", "[1,2]")]
    [DataRow("local s={a=1,b=2}; s.a=nil; s.a=3", "{\"a\":3,\"b\":2}")]
    public async Task RealWorkerIgnoresDeletedEntriesWhenCountingAndChoosingShape(string setup, string expected)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() " + setup + "; ui.set_state(s) end,on_event=function() " +
            "local s=ui.get_state(); ui.set_text(refs.label,s.a==nil and 'absent' or 'present') end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual(expected, session.GetState(instance).ToJson());
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics);
        Assert.AreEqual(expected.Contains("\"a\":", StringComparison.Ordinal) ? "present" : "absent", instance.Definition.Nodes[2].Text);
    }

    [TestMethod]
    [DataRow("local s={1,2,3}; s[2]=nil")]
    [DataRow("local s=ui.list({1,2,3}); s[2]=nil")]
    [DataRow("local s={1,2,3}; s[1]=nil")]
    public async Task RealWorkerRejectsHolesAfterDeletionWithoutPublishingPartialStateOrView(string setup)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() ui.set_state({count=1}) end,on_event=function() " +
            "ui.set_state({count=2}); ui.set_text(refs.label,'bad'); " + setup + "; ui.set_state(s) end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", before.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        var state = session.GetState(instance);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(state, session.GetState(instance));
        Assert.AreSame(before, instance.Definition); Assert.IsFalse(session.IsAvailable(instance));
    }

    [TestMethod]
    public async Task RealWorkerDeletedEntriesDoNotConsumeTheLiveValueQuota()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            return {on_load=function()
              local s={}
              for i=1,1024 do s[i]=false end
              s[1024]=nil
              ui.set_state(s)
            end,on_event=function() end}
            """);
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.AreEqual(1023, session.GetState(instance).Items.Length);
    }

    [TestMethod]
    [DataRow("ui.null.x=42")]
    [DataRow("ui.null[1]=42")]
    [DataRow("ui.null.x=nil")]
    [DataRow("local s=ui.get_state(); s.a.x=42")]
    [DataRow("local s=ui.get_state(); s.list[1].x=42")]
    [DataRow("local s=ui.get_state(); local alias=s.a; alias['x']=42")]
    public async Task RealWorkerNullRejectsWritesImmediatelyWithoutASetStateCall(string mutation)
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, "return {on_load=function() ui.set_state({a=ui.null,b=ui.null,list={ui.null}}) end," +
            "on_event=function() " + mutation + "; ui.set_text(refs.label,'bad') end}",
            ImmutableDictionary<string, Guid>.Empty.Add("label", before.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        var state = session.GetState(instance);
        session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsNotEmpty(session.Diagnostics); Assert.AreSame(state, session.GetState(instance));
        Assert.AreEqual("{\"a\":null,\"b\":null,\"list\":[null]}", state.ToJson());
        Assert.AreSame(before, instance.Definition); Assert.IsFalse(session.IsAvailable(instance));
    }

    [TestMethod]
    public async Task RealWorkerRepeatedNestedReadsPreserveNullIdentityAndExposeNoMembers()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            return {on_load=function() ui.set_state({a=ui.null,b=ui.null,list={ui.null}}) end,
              on_event=function()
                local s=ui.get_state()
                local t=ui.get_state()
                local good=s.a==ui.null and s.b==s.a and t.list[1]==ui.null and t.b==s.a
                good=good and t.a.x==nil and t.b.GetType==nil and t.a.AsString==nil and t.b.Type==nil
                s.a=42
                good=good and ui.get_state().a==ui.null
                ui.set_text(refs.label,good and 'clean' or 'bad')
                ui.set_state(t)
              end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id));
        await ScreenLuaSessionTests.Drain(session);
        for (var i = 0; i < 2; i++)
        {
            session.Enqueue(instance, new("update"), Empty); await ScreenLuaSessionTests.Drain(session);
            Assert.IsEmpty(session.Diagnostics); Assert.AreEqual("clean", instance.Definition.Nodes[2].Text);
            Assert.AreEqual("{\"a\":null,\"b\":null,\"list\":[null]}", session.GetState(instance).ToJson());
        }
    }

    [TestMethod]
    public void AFailedNullWriteCannotContaminateLaterReadsEvenInTheSameVm()
    {
        var script = new Script(CoreModules.None); var projection = new LuaValueProjection(script);
        var state = LuaValue.FromJson("{\"a\":null,\"b\":null,\"list\":[null]}");
        script.Globals.Set("marker", projection.Null);
        script.Globals.Set("snapshot", DynValue.NewCallback((_, _) => projection.Write(state)));
        Assert.Throws<ScriptRuntimeException>(() => script.DoString("local s=snapshot(); s.a.x=42"));
        Assert.IsTrue(script.DoString("local t=snapshot(); return t.a==marker and t.b==marker and t.list[1]==marker and t.b.x==nil").Boolean);
        Assert.AreEqual(state.ToJson(), projection.Read(script.Call(script.Globals.Get("snapshot"))).ToJson());
    }

    [TestMethod]
    public void OpaqueNullDescriptorNeverRegistersReflectionOrAcceptsGeneralClrObjects()
    {
        var script = new Script(CoreModules.None); var projection = new LuaValueProjection(script);
        var marker = projection.Null.UserData; var descriptor = marker.Descriptor;
        Assert.IsFalse(UserData.IsTypeRegistered(descriptor.Type));
        Assert.AreEqual("ui.null", descriptor.Name); Assert.AreEqual("ui.null", descriptor.AsString(marker.Object));
        Assert.IsNull(descriptor.MetaIndex(script, marker.Object, "__call"));
        Assert.IsTrue(descriptor.IsTypeCompatible(descriptor.Type, marker.Object));
        Assert.IsFalse(descriptor.IsTypeCompatible(typeof(object), marker.Object));
        Assert.IsFalse(descriptor.IsTypeCompatible(descriptor.Type, new object()));
    }
}
