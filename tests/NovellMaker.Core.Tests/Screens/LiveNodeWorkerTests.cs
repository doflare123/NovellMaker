using System.Collections.Immutable;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
[DoNotParallelize]
public sealed class LiveNodeWorkerTests
{
    private static (ScreenLuaWorkerService Service, ScreenLuaRequest Load) Worker(string source)
    {
        var definition = ScreenLuaProtocolTests.Definition(); var service = new ScreenLuaWorkerService();
        var hello = ScreenLuaProtocolTests.Request("handshake") with { ResourceId = Guid.Empty, Event = null };
        hello = hello with { Stamp = hello.Stamp with { OwnerId = Guid.Empty, InstanceId = Guid.Empty } };
        Assert.IsNull(service.Process(hello).Error);
        return (service, hello with { Kind = "load", ResourceId = definition.Id, Nodes = definition.Nodes, Source = source,
            Stamp = hello.Stamp with { InstanceId = Guid.NewGuid(), OwnerId = Guid.NewGuid() },
            References = ImmutableDictionary<string, Guid>.Empty.Add("root", definition.Nodes[0].Id).Add("button", definition.Nodes[1].Id).Add("label", definition.Nodes[2].Id) });
    }
    private static ScreenLuaRequest Event(ScreenLuaRequest load, long version = 1) => load with { Kind = "event", Source = null, Nodes = [],
        References = ImmutableDictionary<string, Guid>.Empty, Event = new("click", load.Nodes[1].Id), Stamp = load.Stamp with { RequestId = Guid.NewGuid(), Version = version } };

    [TestMethod]
    public void WorkerCreatesInstanceHandlesAndCanModifyReparentFindAndRemoveAcrossEvents()
    {
        var (service, load) = Worker("""
            local copy
            return {
              on_load=function()
                local nodes
                copy,nodes=ui.create(refs.button,refs.root)
                ui.set_text(nodes[refs.button], 'Живой объект')
                ui.set_geometry(copy,{x=20,y=30,width=45,height=25,anchor_x=0.1,anchor_y=0.2,pivot_x=0.5,pivot_y=0.5,scale_x=-1,scale_y=1,rotation=15,opacity=0.7,color='#80FFFF',order=2,clip_children=false})
                ui.set_parent(copy,refs.root)
              end,
              on_event=function(event)
                if event.node==refs.button and ui.find('label')==refs.label and ui.find('missing')==nil then
                  ui.remove(copy)
                  ui.remove(refs.label)
                  if ui.find('label')==nil then ui.set_text(refs.button,'Удалено') end
                end
              end
            }
            """);
        var response = service.Process(load); Assert.IsNull(response.Error);
        var definition = new ScreenDefinition(load.ResourceId, load.Nodes);
        var prepared = ScreenLuaProtocol.Prepare(definition, response.Changes).Definition;
        var created = prepared.Nodes.Last(); Assert.AreEqual("Живой объект", created.Text); Assert.AreNotEqual(load.Nodes[1].Id, created.Id);
        Assert.AreEqual(15, created.Geometry.Rotation); Assert.AreEqual(-1, created.Geometry.ScaleX); Assert.IsFalse(created.ClipChildren);
        response = service.Process(Event(load)); Assert.IsNull(response.Error);
        prepared = ScreenLuaProtocol.Prepare(prepared, response.Changes).Definition;
        Assert.HasCount(2, prepared.Nodes); Assert.AreEqual("Удалено", prepared.Nodes[1].Text);
    }

    [TestMethod]
    [DataRow("{x=0/0}")][DataRow("{y=1/0}")][DataRow("{width='bad'}")][DataRow("{height=0}")]
    [DataRow("{unknown=1}")][DataRow("{[1]=2}")][DataRow("{color=3}")][DataRow("{color='red'}")]
    [DataRow("{clip_children=1}")][DataRow("{order=0.5}")][DataRow("{order=100001}")][DataRow("{scale_x=0}")]
    public void InvalidGeometryRejectsPriorCommandsAndDiscardsMutatedVm(string patch)
    {
        var (service, load) = Worker("return {on_event=function() ui.set_text(refs.label,'partial'); ui.set_geometry(refs.button," + patch + ") end}");
        Assert.IsNull(service.Process(load).Error);
        var response = service.Process(Event(load)); Assert.IsNotNull(response.Error); Assert.IsEmpty(response.Changes);
        Assert.IsNotNull(service.Process(Event(load)).Error);
    }

    [TestMethod]
    [DataRow("ui.remove(refs.label);ui.set_text(refs.label,'deleted')")]
    [DataRow("ui.set_parent(refs.root,refs.root)")]
    [DataRow("ui.set_parent(refs.button,refs.button)")]
    [DataRow("ui.remove(refs.root)")]
    [DataRow("ui.create(refs.root,refs.button)")]
    [DataRow("ui.set_text('00000000000000000000000000000001/00000000000000000000000000000002/00000000000000000000000000000003','foreign')")]
    public void InvalidLifetimeOrOwnerRejectsWholeHandler(string body)
    {
        var (service, load) = Worker("return {on_event=function() ui.set_text(refs.label,'partial');" + body + " end}");
        Assert.IsNull(service.Process(load).Error); var response = service.Process(Event(load)); Assert.IsNotNull(response.Error); Assert.IsEmpty(response.Changes);
    }

    [TestMethod]
    public void HandleOfAnotherInstanceOfSameResourceCannotChangeOrCreateNodes()
    {
        var (service, first) = Worker("return {on_event=function() ui.set_text(refs.label,'first alive') end}");
        Assert.IsNull(service.Process(first).Error);
        var foreign = new ScreenNodeHandle(first.ResourceId, first.Stamp.InstanceId, first.Nodes[1].Id).ToString();
        foreach (var operation in new[] { "ui.set_text('" + foreign + "','foreign')", "ui.create('" + foreign + "',refs.root)", "ui.set_parent(refs.button,'" + foreign + "')" })
        {
            var other = first with { Stamp = first.Stamp with { InstanceId = Guid.NewGuid() }, Source = "return {on_event=function() ui.set_text(refs.label,'partial');" + operation + " end}" };
            Assert.IsNull(service.Process(other).Error);
            var response = service.Process(Event(other)); Assert.IsNotNull(response.Error); Assert.IsEmpty(response.Changes);
        }
        Assert.AreEqual("first alive", service.Process(Event(first)).Changes.Single().Text);
    }

    [TestMethod]
    public async Task RealWorkerChangesGeometryOnlyAfterHostPollAndCanRemoveSubtrees()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        await using var session = new ScreenLuaSession(stack);
        session.Attach(instance, """
            local copy
            return {on_load=function() copy=ui.create(refs.button,refs.root);ui.set_geometry(copy,{x=5,y=70,rotation=20}) end,
              on_event=function() ui.remove(copy);ui.set_geometry(refs.button,{scale_x=-1,pivot_x=0.5});ui.set_text(refs.label,'Готово') end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("root", before.Nodes[0].Id).Add("button", before.Nodes[1].Id).Add("label", before.Nodes[2].Id));
        session.Poll(); await session.PendingTask!; Assert.AreSame(before, instance.Definition);
        await ScreenLuaSessionTests.Drain(session); Assert.HasCount(4, instance.Definition.Nodes); Assert.AreEqual(20, instance.Definition.Nodes.Last().Geometry.Rotation);
        Assert.IsTrue(session.Diagnostics.IsEmpty);
        session.EnqueueUsingInstanceSnapshot(instance, new("update")); await ScreenLuaSessionTests.Drain(session);
        Assert.HasCount(3, instance.Definition.Nodes); Assert.AreEqual(-1, instance.Definition.Nodes[1].Geometry.ScaleX); Assert.AreEqual("Готово", instance.Definition.Nodes[2].Text);
        Assert.IsTrue(session.Diagnostics.IsEmpty);
    }

    [TestMethod]
    public async Task CommitBudgetRaceDisablesOnlyItsHandlerAndClosesRemoteVmWithoutPartialChanges()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var before = instance.Definition;
        var connection = new ScreenLuaSessionTests.Connection();
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {}"); session.Poll(); var call = await connection.Next();
        var root = before.Nodes[0]; var button = before.Nodes[1];
        call.Reply(new ScreenChange("copy", button.Id, null, null) { ParentId = root.Id, Ids = ImmutableDictionary<Guid, Guid>.Empty.Add(button.Id, Guid.NewGuid()) });
        await session.PendingTask!;
        var large = new ScreenDefinition(Guid.NewGuid(), [root, .. Enumerable.Range(0, 1023).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Area, new(0, 0, 1, 1)))]);
        for (var i = 0; i < 3; i++) stack.Open(Guid.NewGuid(), large);
        stack.Open(Guid.NewGuid(), new ScreenDefinition(Guid.NewGuid(), [root, .. Enumerable.Range(0, 1020).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Area, new(0, 0, 1, 1)))]));
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreSame(before, instance.Definition); Assert.AreEqual(0L, instance.Version); Assert.IsFalse(session.IsAvailable(instance));
        Assert.AreEqual(ScreenLuaIssueKind.Handler, session.Issues.Single().Kind); Assert.IsFalse(connection.Disposed);
        Assert.IsTrue(connection.Seen.Any(r => r.Kind == "close" && r.Stamp.Version == 1));
    }

    [TestMethod]
    public void WorkerAggregateCreationBudgetAccountsForNewNodesOnLoadAndEvent()
    {
        var (service, load) = Worker("return {on_event=function() ui.create(refs.button,refs.root) end}");
        var root = load.Nodes[0];
        var nodes = load.Nodes.AddRange(Enumerable.Range(0, 1019).Select(_ => new ScreenNode(Guid.NewGuid(), root.Id, ScreenNodeKind.Area, new(0, 0, 1, 1))));
        var loaded = new List<ScreenLuaRequest>();
        for (var i = 0; i < 4; i++)
        { var item = load with { Nodes = nodes, Stamp = load.Stamp with { InstanceId = Guid.NewGuid() } }; loaded.Add(item); Assert.IsNull(service.Process(item).Error); }
        for (var i = 0; i < 4; i++) Assert.IsNull(service.Process(Event(loaded[i])).Error);
        for (var i = 0; i < 4; i++) Assert.IsNull(service.Process(Event(loaded[i], 2)).Error);
        Assert.IsNotNull(service.Process(Event(loaded[0], 3)).Error);
    }
}
