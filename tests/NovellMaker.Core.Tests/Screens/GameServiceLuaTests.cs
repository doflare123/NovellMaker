using System.Collections.Immutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;
using NovellMaker.Core.Profiles;

namespace NovellMaker.Core.Tests.Screens;

[TestClass]
public sealed class GameServiceLuaTests
{
    [TestMethod]
    [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)] [DataRow(6)] [DataRow(7)]
    public void SettingsWireExplicitlyRejectsEveryEarlierProtocol(int protocol)
    {
        var worker = new ScreenLuaWorkerService();
        var request = ScreenLuaProtocolTests.Request("handshake") with { Protocol = protocol };
        var response = worker.Process(request);
        Assert.AreEqual("error", response.Kind); Assert.AreEqual(8, response.Protocol);
        Assert.IsEmpty(response.Changes);
    }

    private sealed class FramedWorker(bool realWorker) : IScreenLuaConnection
    {
        private readonly ScreenLuaWorkerService worker = new();
        private readonly ScreenLuaConnection? connection = realWorker ? new() : null;
        internal readonly List<ScreenLuaRequest> Seen = [];
        public async Task<ScreenLuaResponse> ExchangeAsync(ScreenLuaRequest request, CancellationToken token)
        {
            using var wire = new MemoryStream();
            await ScreenLuaProtocol.WriteAsync(wire, request, token);
            wire.Position = 0;
            var received = await ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(wire, token);
            Seen.Add(received);
            return connection is null ? worker.Process(received) : await connection.ExchangeAsync(received, token);
        }
        public ValueTask DisposeAsync() => connection?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    [TestMethod]
    [DataRow(0, false)] [DataRow(12, false)] [DataRow(16, false)]
    [DataRow(0, true)] [DataRow(12, true)] [DataRow(16, true)]
    public async Task SlotResultAndVariableSnapshotShareFrameBudgetWithoutLosingSlotsOrLuaSession(int variableCount, bool diagnostics)
    {
        var metadata = new GameSlotMetadata(Guid.NewGuid(), new string('я', 128), DateTimeOffset.UtcNow,
            string.Concat(Enumerable.Repeat("я😀", 42)), Guid.NewGuid(), new string('я', 256), Guid.NewGuid(), new string('я', 512), 0);
        var items = Enumerable.Range(0, 128).Select(i => new GameSlotInfo(("slot-" + i).PadRight(64, 'x'),
            i % 2 == 0 ? GameSlotStatus.Compatible : GameSlotStatus.Corrupt, metadata, diagnostics ? new string('я', 1024) : null)).ToImmutableArray();
        var result = new GameServiceResult(Guid.NewGuid(), GameServiceAction.ListSlots, true, new string('я', 1024), null, items);
        result.Validate();
        var snapshot = ImmutableDictionary<string, string>.Empty;
        var remaining = LuaLimits.MaximumVariableCharacters;
        for (var i = 0; i < variableCount; i++)
        {
            var key = "var-" + i; var length = Math.Min(8192, remaining - key.Length);
            snapshot = snapshot.Add(key, new string('я', length)); remaining -= key.Length + length;
        }
        Assert.IsTrue(LuaLimits.ValidVariables(snapshot));
        if (variableCount == 16) Assert.AreEqual(0, remaining);
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        var other = stack.Open(Guid.NewGuid(), instance.Definition);
        var worker = new FramedWorker(variableCount == 16 && diagnostics); await using var session = new ScreenLuaSession(stack, () => worker);
        const string source = """
            return {on_event=function(e)
              if e.kind=='service_result' then
                ui.set_text(refs.label,e.slots[128].id..':'..#e.slots)
              else ui.set_text(refs.label,'alive') end
            end}
            """;
        var refs = ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id);
        session.Attach(instance, source, refs); session.Attach(other, source, refs);
        await ScreenLuaSessionTests.Drain(session);
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.ReserveServiceReply(instance, new(result.RequestId, result.Action)));
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(instance, new("service_result") { Result = result }, snapshot));
        await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.Diagnostics); Assert.IsTrue(session.IsAvailable(instance)); Assert.IsTrue(session.IsAvailable(other));
        Assert.AreEqual(items[127].Id + ":128", instance.Definition.Nodes[2].Text);
        var received = worker.Seen.Single(r => r.Event?.Kind == "service_result");
        Assert.AreSequenceEqual(snapshot.OrderBy(p => p.Key), received.Snapshot.OrderBy(p => p.Key));
        var projected = received.Event!.Result!; projected.Validate(); Assert.AreEqual(result.RequestId, projected.RequestId);
        Assert.AreEqual(result.Success, projected.Success); Assert.AreEqual(result.Action, projected.Action);
        Assert.HasCount(128, projected.Slots);
        if (variableCount > 0 || diagnostics) StringAssert.Contains(projected.Message, "сокращён");
        else Assert.AreEqual(result.Message, projected.Message);
        for (var i = 0; i < items.Length; i++)
        {
            Assert.AreEqual(items[i].Id, projected.Slots[i].Id); Assert.AreEqual(items[i].Status, projected.Slots[i].Status);
            var actual = projected.Slots[i].Metadata!;
            Assert.AreEqual(metadata, actual with { Title = metadata.Title, SceneName = metadata.SceneName, Phrase = metadata.Phrase });
            Assert.IsTrue(metadata.Title.StartsWith(actual.Title, StringComparison.Ordinal));
            Assert.IsFalse(actual.Title.Length > 0 && char.IsHighSurrogate(actual.Title[^1]));
        }
        Assert.AreSequenceEqual(items, result.Slots); Assert.AreEqual(metadata, result.Slots[0].Metadata);
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(instance, new("update"), snapshot));
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(other, new("update"), snapshot));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        Assert.AreEqual("alive", instance.Definition.Nodes[2].Text); Assert.AreEqual("alive", other.Definition.Nodes[2].Text);
    }

    [TestMethod]
    public async Task MaximumSlotProjectionFitsOneBoundedWireFrameAndOversizedFieldsAreRejected()
    {
        var metadata = new GameSlotMetadata(Guid.NewGuid(), new string('a',128), DateTimeOffset.UtcNow,
            new string('я',128), Guid.NewGuid(), new string('я',256), null, new string('я',512), 0);
        var items = Enumerable.Range(0,128).Select(i => new GameSlotInfo("slot-"+i,GameSlotStatus.Compatible,metadata,null)).ToImmutableArray();
        var result = new GameServiceResult(Guid.NewGuid(),GameServiceAction.ListSlots,true,new string('я',1024),null,items);
        result.Validate();
        var request = ScreenLuaProtocolTests.Request() with { Event = new("service_result") { Result = result } };
        using var wire = new MemoryStream(); await ScreenLuaProtocol.WriteAsync(wire,request,CancellationToken.None);
        Assert.IsLessThanOrEqualTo(ScreenLuaProtocol.MaximumFrameBytes+4,wire.Length);
        Assert.Throws<ArgumentException>(() => (result with { Slots=items.Add(items[0]) }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Slots=items.SetItem(0,items[0] with { Metadata=metadata with { Title=new string('x',129) } }) }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Slots=items.SetItem(0,items[0] with { Status=(GameSlotStatus)999 }) }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Slots=default }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Action=GameServiceAction.LoadSlot }).Validate());
    }

    [TestMethod]
    [DataRow("ui.new_game()", GameServiceAction.NewGame)]
    [DataRow("ui.main_menu()", GameServiceAction.MainMenu)]
    [DataRow("ui.end_session()", GameServiceAction.EndSession)]
    [DataRow("ui.continue_game()", GameServiceAction.ContinueGame)]
    [DataRow("ui.list_slots()", GameServiceAction.ListSlots)]
    [DataRow("ui.save_slot('chapter-1','Моя глава')", GameServiceAction.SaveSlot)]
    [DataRow("ui.save_slot()", GameServiceAction.SaveSlot)]
    [DataRow("ui.load_slot('chapter-1')", GameServiceAction.LoadSlot)]
    [DataRow("ui.delete_slot('chapter-1')", GameServiceAction.DeleteSlot)]
    [DataRow("ui.get_settings()", GameServiceAction.GetSettings)]
    [DataRow("ui.set_setting('master_volume',0.5)", GameServiceAction.SetSetting)]
    [DataRow("ui.set_setting('fullscreen',true)", GameServiceAction.SetSetting)]
    public void LuaReturnsQueuedCommandAndRequestIdWithoutExecutingHost(string expression, GameServiceAction action)
    {
        var definition = ScreenLuaProtocolTests.Definition();
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), "return {on_load=function() local id=" + expression +
            "; ui.set_text(refs.label,id) end,on_event=function(e) end}", ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id));
        var changes = vm.Handle(null, ImmutableDictionary<string, string>.Empty);
        var batch = ScreenLuaProtocol.Prepare(definition, changes);
        Assert.AreEqual(action, batch.Service!.Action); Assert.AreNotEqual(Guid.Empty, batch.Service.RequestId);
        Assert.AreEqual(batch.Service.RequestId.ToString("N"), batch.Definition.Nodes[2].Text);
        if (expression.Contains("chapter-1", StringComparison.Ordinal)) Assert.AreEqual("chapter-1", batch.Service.SlotId);
        if (expression.Contains("Моя глава", StringComparison.Ordinal)) Assert.AreEqual("Моя глава", batch.Service.Title);
    }

    [TestMethod]
    [DataRow("ui.load_slot('../outside')")] [DataRow("ui.save_slot('CON')")]
    [DataRow("ui.delete_slot('')")] [DataRow("ui.new_game('not-a-scene')")]
    [DataRow("ui.set_setting('unknown',true)")] [DataRow("ui.set_setting('master_volume',2)")]
    [DataRow("ui.set_setting('master_volume','0.5')")] [DataRow("ui.set_setting('fullscreen',1)")]
    [DataRow("ui.set_setting('sound_volume',0/0)")] [DataRow("ui.set_setting('voice_volume',true)")]
    public void InvalidLuaArgumentsAreRejectedBeforeCommandCanReachHost(string expression)
    {
        var vm = new ScreenLuaVm(ScreenLuaProtocolTests.Definition(), Guid.NewGuid(),
            "return {on_event=function(e) " + expression + " end}", ImmutableDictionary<string, Guid>.Empty);
        Assert.Throws<Exception>(() => vm.Handle(new("update"), ImmutableDictionary<string, string>.Empty));
    }

    [TestMethod]
    public async Task SettingsTravelThroughRealWorkerAndOnlyValidatedAtomicBatchesReachHost()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture();
        var worker = new FramedWorker(true); await using var session = new ScreenLuaSession(stack, () => worker);
        var references = ImmutableDictionary<string, Guid>.Empty.Add("label", instance.Definition.Nodes[2].Id);
        session.Attach(instance, """
            return {on_load=function() ui.get_settings() end,on_event=function(e)
              if e.kind=='service_result' then
                ui.set_text(refs.label,e.settings.voice_muted and 'muted' or 'audible')
                ui.set_setting('sound_volume',e.settings.master_volume)
              end
            end}
            """, references);
        await ScreenLuaSessionTests.Drain(session); var request = session.DrainServices().Single().Command;
        Assert.AreEqual(GameServiceAction.GetSettings, request.Action);
        var value = PlayerSettings.Defaults(new()) with { Master = new(.25f, false), Voice = new(1, true) };
        var result = new GameServiceResult(request.RequestId, request.Action, true, "", null, []) { Settings = value, ProfileSaved = true };
        Assert.AreEqual(ScreenEventAdmission.Accepted, session.Enqueue(instance, new("service_result") { Result = result }, ImmutableDictionary<string, string>.Empty));
        await ScreenLuaSessionTests.Drain(session); Assert.IsEmpty(session.Diagnostics);
        Assert.AreEqual("muted", instance.Definition.Nodes[2].Text);
        var command = session.DrainServices().Single().Command;
        Assert.AreEqual(new("sound_volume", .25, null), command.Setting);
        Assert.Throws<ArgumentException>(() => (command with { Setting = null }).Validate());
        Assert.Throws<ArgumentException>(() => (command with { Action = GameServiceAction.GetSettings }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Settings = null }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Action = GameServiceAction.NewGame }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Settings = value with { Effect = new(-1, false) } }).Validate());
        var definition = ScreenLuaProtocolTests.Definition();
        foreach (var code in new[] { "ui.set_setting('fullscreen',true); ui.close()", "ui.get_settings(); ui.get_settings()",
            "ui.set_setting('fullscreen',true); ui.set_text(refs.label,'changed'); ui.set_setting('bad',true)" })
        {
            var vm = new ScreenLuaVm(definition, Guid.NewGuid(), "return {on_event=function(e) " + code + " end}", references);
            Assert.Throws<Exception>(() => ScreenLuaProtocol.Prepare(definition, vm.Handle(new("update"), ImmutableDictionary<string, string>.Empty)));
            Assert.AreNotEqual("changed", definition.Nodes[2].Text);
        }
    }

    [TestMethod]
    public void ServiceResultContainsMetadataAndRequestIdentityInLua()
    {
        var definition = ScreenLuaProtocolTests.Definition(); var id = Guid.NewGuid();
        var vm = new ScreenLuaVm(definition, Guid.NewGuid(), """
            return {on_event=function(e)
              if e.kind=='service_result' and e.success and e.operation=='ListSlots' and #e.slots==1 then
                ui.set_text(refs.label,e.request_id..':'..e.slots[1].id..':'..e.slots[1].title..':'..e.slots[1].phrase)
              end
            end}
            """, ImmutableDictionary<string, Guid>.Empty.Add("label", definition.Nodes[2].Id));
        var metadata = new GameSlotMetadata(Guid.NewGuid(), "revision", DateTimeOffset.UtcNow, "Глава", Guid.NewGuid(), "Сцена", null, "Фраза", 3);
        var value = new ScreenLuaEvent("service_result") { Result = new(id, GameServiceAction.ListSlots, true, "Список", null,
            [new("slot-1", GameSlotStatus.Compatible, metadata, null)]) };
        ScreenLuaProtocol.ValidateEvent(definition, value);
        var batch = ScreenLuaProtocol.Prepare(definition, vm.Handle(value, ImmutableDictionary<string, string>.Empty));
        Assert.AreEqual(id.ToString("N") + ":slot-1:Глава:Фраза", batch.Definition.Nodes[2].Text);
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(definition, value with { NodeId = definition.Nodes[1].Id }));
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(definition, value with { Kind = "click", NodeId = definition.Nodes[1].Id }));
        Assert.Throws<ArgumentException>(() => ScreenLuaProtocol.ValidateEvent(definition, new("service_result")));
    }

    [TestMethod]
    public void ServiceBatchRejectsMultipleCommandsCloseAndMalformedFieldsAtomically()
    {
        var definition = ScreenLuaProtocolTests.Definition(); var service = new ScreenChange("service", null, null, null)
            { Service = new(Guid.NewGuid(), GameServiceAction.ListSlots) };
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [service, service]));
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [service, new("close", null, null, null)]));
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [service with { NodeId = definition.Nodes[1].Id }]));
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [service with { Kind = "text", NodeId = definition.Nodes[2].Id, Text = "text" }]));
        Assert.Throws<InvalidOperationException>(() => ScreenLuaProtocol.Prepare(definition, [service, new("bogus", definition.Nodes[1].Id, null, null)]));
    }

    [TestMethod]
    public async Task CommandsAreExposedOnlyAfterValidCommitAndAreDroppedWhenOwnerCloses()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var command = new GameServiceCommand(Guid.NewGuid(), GameServiceAction.ListSlots);
        var connection = new ScreenLuaSessionTests.Connection(automatic: true)
        { Response = request => ScreenLuaProtocolTests.Reply(request, request.Kind == "load" ? [new("service", null, null, null) { Service = command }] : []) };
        await using var session = new ScreenLuaSession(stack, () => connection);
        session.Attach(instance, "return {on_event=function(e) end}"); await ScreenLuaSessionTests.Drain(session);
        var invocation = session.DrainServices().Single(); Assert.AreSame(instance, invocation.Instance); Assert.AreEqual(command, invocation.Command);
        session.Enqueue(instance, new("update"), ImmutableDictionary<string, string>.Empty); await ScreenLuaSessionTests.Drain(session);
        Assert.IsEmpty(session.DrainServices());
        var another = stack.Open(instance.OwnerId, instance.Definition); session.Attach(another, "return {on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(session); stack.Close(another); Assert.IsEmpty(session.DrainServices());
    }

    [TestMethod]
    public async Task InvalidServicePacketDoesNotCommitUiOrHostEffect()
    {
        var (stack, instance) = ScreenLuaSessionTests.Fixture(); var original = instance.Definition;
        var connection = new ScreenLuaSessionTests.Connection(automatic: true) { Response = request => ScreenLuaProtocolTests.Reply(request,
            request.Kind == "load" ? [new("text", original.Nodes[2].Id, "must not apply", null),
                new("service", null, null, null) { Service = new(Guid.Empty, GameServiceAction.MainMenu) }] : []) };
        await using var session = new ScreenLuaSession(stack, () => connection); session.Attach(instance, "return {on_event=function(e) end}");
        await ScreenLuaSessionTests.Drain(session); Assert.AreSame(original, instance.Definition); Assert.IsEmpty(session.DrainServices());
        Assert.IsNotEmpty(session.Diagnostics);
    }

    [TestMethod]
    public async Task Version18SlotBindingsRoundTripAndVersion17RejectsFieldsAndCommands()
    {
        var scene = ScreenFixture.Create("Сцена").ToScene(); var node = scene.Canvas.Nodes[2].Id;
        scene = scene with { Canvas = scene.Canvas with { Buttons = [new(node, ScreenButtonAction.SaveSlot, null) { SlotId = "chapter-1", SlotTitle = "Глава" }] } };
        var project = NovelProject.Create("Слоты") with { Scenes = [scene], InputBindings = [new("F3", ScreenButtonAction.ContinueGame, null)] };
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".novell");
        try
        {
            var store = new JsonProjectStore(); await store.SaveAsync(path, project); var text = File.ReadAllText(path);
            Assert.IsTrue(project.ContentEquals(await store.LoadAsync(path)));
            var (copy, _) = SceneCopy.Create(scene); Assert.AreEqual("chapter-1", copy.Canvas.Buttons[0].SlotId); Assert.AreEqual("Глава", copy.Canvas.Buttons[0].SlotTitle);
            var doc = JsonNode.Parse(text)!; doc["scenes"]![0]!["canvas"]!["buttons"]![0]!.AsObject().Remove("slotId");
            File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            doc = JsonNode.Parse(text)!; doc["formatVersion"] = 17;
            File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            var binding = doc["scenes"]![0]!["canvas"]!["buttons"]![0]!; binding.AsObject().Remove("slotId"); binding.AsObject().Remove("slotTitle");
            File.WriteAllText(path, doc.ToJsonString()); await Assert.ThrowsAsync<ProjectFormatException>(() => store.LoadAsync(path));
            binding["action"] = (int)ScreenButtonAction.Load; doc["inputBindings"] = new JsonArray();
            var legacy = doc.ToJsonString(); File.WriteAllText(path, legacy);
            var migrated = await store.LoadAsync(path); Assert.AreEqual(18, migrated.FormatVersion); Assert.AreEqual(legacy, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
