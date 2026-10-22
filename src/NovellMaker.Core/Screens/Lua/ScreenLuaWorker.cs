using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;

namespace NovellMaker.Core.Screens.Lua;

/// <summary>Only the isolated --screen-lua-worker process uses this VM.</summary>
internal sealed class ScreenLuaVm
{
    private readonly Script script = new(CoreModules.None);
    private readonly DynValue handlers;
    private readonly Guid instanceId;
    private ImmutableArray<ScreenChange>.Builder changes = ImmutableArray.CreateBuilder<ScreenChange>();
    private ImmutableDictionary<string, string> snapshot = ImmutableDictionary<string, string>.Empty;
    internal ScreenDefinition Definition { get; private set; }

    internal ScreenLuaVm(ScreenDefinition definition, Guid instanceId, string source, ImmutableDictionary<string, Guid> references)
    {
        Definition = definition;
        this.instanceId = instanceId;
        LuaLimits.CheckSource(source);
        var ui = new Table(script);
        var refs = new Table(script);
        string Handle(Guid id) => new ScreenNodeHandle(definition.Id, instanceId, id).ToString();
        foreach (var pair in references) refs.Set(pair.Key, DynValue.NewString(Handle(pair.Value)));
        script.Globals.Set("refs", DynValue.NewTable(refs));
        void Bind(string name, Func<CallbackArguments, DynValue> callback) => ui.Set(name, DynValue.NewCallback((_, args) => callback(args)));
        string Str(CallbackArguments args, int index) => args.AsType(index, "ui", DataType.String, false).String;
        Guid Node(CallbackArguments args, int index = 0) => ScreenNodeHandle.TryParse(Str(args, index), out var handle) && handle.ResourceId == definition.Id && handle.InstanceId == instanceId &&
            Definition.Nodes.Any(n => n.Id == handle.NodeId) ? handle.NodeId : throw new ScriptRuntimeException("Чужой или удалённый узел экрана.");
        DynValue Add(ScreenChange change)
        {
            if (changes.Count >= LuaActionScript.MaximumCommands) throw new ScriptRuntimeException("Превышен лимит изменений экрана.");
            Definition = ScreenLuaProtocol.Prepare(Definition, [change]).Definition;
            changes.Add(change); return DynValue.Nil;
        }
        Bind("set_text", a => Add(new("text", Node(a), Str(a, 1), null)));
        Bind("set_visible", a => Add(new("visible", Node(a), null, a.AsType(1, "ui", DataType.Boolean, false).Boolean)));
        Bind("set_enabled", a => Add(new("enabled", Node(a), null, a.AsType(1, "ui", DataType.Boolean, false).Boolean)));
        Bind("set_geometry", a => Add(new("geometry", Node(a), null, null) { Patch = ReadPatch(a.AsType(1, "ui", DataType.Table, false).Table) }));
        Bind("set_parent", a => Add(new("parent", Node(a), null, null) { ParentId = Node(a, 1) }));
        Bind("remove", a => Add(new("remove", Node(a), null, null)));
        Bind("find", a => references.TryGetValue(Str(a, 0), out var id) && Definition.Nodes.Any(n => n.Id == id) ? DynValue.NewString(Handle(id)) : DynValue.Nil);
        Bind("create", a =>
        {
            var sourceId = Node(a); var parentId = Node(a, 1);
            var copy = ScreenTree.Copy(Definition, sourceId, parentId);
            Add(new("copy", sourceId, null, null) { ParentId = parentId, Ids = copy.Ids });
            var mapped = new Table(script);
            foreach (var pair in copy.Ids) mapped.Set(Handle(pair.Key), DynValue.NewString(Handle(pair.Value)));
            return DynValue.NewTuple(DynValue.NewString(Handle(copy.Ids[sourceId])), DynValue.NewTable(mapped));
        });
        Bind("close", _ => Add(new("close", null, null, null)));
        Bind("get", a => snapshot.TryGetValue(Str(a, 0), out var value) ? DynValue.NewString(value) : DynValue.Nil);
        string? Optional(CallbackArguments a, int index) => a[index].IsNil() ? null : Str(a, index);
        DynValue Service(GameServiceAction action, string? slot = null, string? title = null, Guid? scene = null, PlayerSettingChange? setting = null)
        {
            var command = new GameServiceCommand(Guid.NewGuid(), action, slot, title, scene) { Setting = setting };
            command.Validate(); Add(new("service", null, null, null) { Service = command });
            return DynValue.NewString(command.RequestId.ToString("N"));
        }
        Bind("new_game", a => Service(GameServiceAction.NewGame, scene: Optional(a, 0) is { } id
            ? Guid.TryParse(id, out var parsed) && parsed != Guid.Empty ? parsed : throw new ScriptRuntimeException("Ожидался ID сцены.") : null));
        Bind("main_menu", _ => Service(GameServiceAction.MainMenu));
        Bind("end_session", _ => Service(GameServiceAction.EndSession));
        Bind("continue_game", _ => Service(GameServiceAction.ContinueGame));
        Bind("list_slots", _ => Service(GameServiceAction.ListSlots));
        Bind("save_slot", a => Service(GameServiceAction.SaveSlot, Optional(a, 0), Optional(a, 1)));
        Bind("load_slot", a => Service(GameServiceAction.LoadSlot, Str(a, 0)));
        Bind("delete_slot", a => Service(GameServiceAction.DeleteSlot, Str(a, 0)));
        Bind("get_settings", _ => Service(GameServiceAction.GetSettings));
        Bind("set_setting", a =>
        {
            var value = a[1];
            if (value.Type is not (DataType.Number or DataType.Boolean)) throw new ScriptRuntimeException("Настройка ожидает число или boolean.");
            return Service(GameServiceAction.SetSetting, setting: new(Str(a, 0),
                value.Type == DataType.Number ? value.Number : null, value.Type == DataType.Boolean ? value.Boolean : null));
        });
        script.Globals.Set("ui", DynValue.NewTable(ui));
        handlers = Execute(script.LoadString(source, null, "screen.lua"));
        if (handlers.Type != DataType.Table || handlers.Table.Get("on_event").Type != DataType.Function ||
            handlers.Table.Get("on_load").Type is not (DataType.Nil or DataType.Function))
            throw new InvalidOperationException("Экранный Lua должен вернуть таблицу с функцией on_event и необязательной on_load.");
        if (changes.Count != 0) throw new InvalidOperationException("Изменения экрана и команды сервисов разрешены только в обработчике.");
    }

    internal ImmutableArray<ScreenChange> Handle(ScreenLuaEvent? value, ImmutableDictionary<string, string> data)
    {
        snapshot = data;
        changes = ImmutableArray.CreateBuilder<ScreenChange>();
        var function = handlers.Table.Get(value is null ? "on_load" : "on_event");
        var argument = new Table(script);
        if (value is not null)
        {
            argument.Set("kind", DynValue.NewString(value.Kind));
            argument.Set("node", value.NodeId is { } node ? DynValue.NewString(new ScreenNodeHandle(Definition.Id, instanceId, node).ToString()) : DynValue.Nil);
            argument.Set("x", DynValue.NewNumber(value.X)); argument.Set("y", DynValue.NewNumber(value.Y));
            argument.Set("seconds", DynValue.NewNumber(value.Seconds));
            if (value.Result is { } result)
            {
                result.Validate();
                argument.Set("request_id", DynValue.NewString(result.RequestId.ToString("N")));
                argument.Set("operation", DynValue.NewString(result.Action.ToString()));
                argument.Set("success", DynValue.NewBoolean(result.Success));
                argument.Set("message", DynValue.NewString(result.Message));
                argument.Set("slot_id", result.SlotId is { } slot ? DynValue.NewString(slot) : DynValue.Nil);
                if (result.Settings is { } settings)
                {
                    var values = new Table(script);
                    void Audio(string key, PlayerAudioSettings bus)
                    { values.Set(key + "_volume", DynValue.NewNumber(bus.Volume)); values.Set(key + "_muted", DynValue.NewBoolean(bus.Muted)); }
                    Audio("master", settings.Master); Audio("sound", settings.Sound); Audio("effect", settings.Effect); Audio("voice", settings.Voice);
                    values.Set("fullscreen", DynValue.NewBoolean(settings.Fullscreen));
                    argument.Set("settings", DynValue.NewTable(values));
                }
                var slots = new Table(script);
                foreach (var item in result.Slots)
                {
                    var entry = new Table(script);
                    entry.Set("id", DynValue.NewString(item.Id)); entry.Set("status", DynValue.NewString(item.Status.ToString()));
                    entry.Set("compatible", DynValue.NewBoolean(item.Status == GameSlotStatus.Compatible));
                    entry.Set("legacy", DynValue.NewBoolean(item.Legacy));
                    entry.Set("diagnostic", item.Diagnostic is { } diagnostic ? DynValue.NewString(diagnostic) : DynValue.Nil);
                    if (item.Metadata is { } metadata)
                    {
                        entry.Set("title", DynValue.NewString(metadata.Title)); entry.Set("scene", DynValue.NewString(metadata.SceneName));
                        entry.Set("phrase", DynValue.NewString(metadata.Phrase)); entry.Set("revision", DynValue.NewString(metadata.Revision));
                        entry.Set("saved_at", DynValue.NewString(metadata.SavedAtUtc.ToString("O")));
                        entry.Set("game_time", DynValue.NewNumber(metadata.GameTime));
                    }
                    slots.Set(slots.Length + 1, DynValue.NewTable(entry));
                }
                argument.Set("slots", DynValue.NewTable(slots));
            }
        }
        if (function.Type != DataType.Nil) Execute(function, DynValue.NewTable(argument));
        var batch = changes.ToImmutable();
        // Revalidate aggregate text/command budgets as one atomic batch on the host too.
        return batch;
    }

    private static ScreenNodePatch ReadPatch(Table table)
    {
        string[] names = ["x", "y", "width", "height", "anchor_x", "anchor_y", "pivot_x", "pivot_y", "scale_x", "scale_y", "rotation", "opacity", "color", "order", "clip_children"];
        foreach (var pair in table.Pairs)
            if (pair.Key.Type != DataType.String || !names.Contains(pair.Key.String)) throw new ScriptRuntimeException("Неизвестное свойство геометрии.");
        double? Number(string key)
        {
            var value = table.Get(key);
            if (value.IsNil()) return null;
            if (value.Type != DataType.Number || !double.IsFinite(value.Number)) throw new ScriptRuntimeException("Свойство должно быть конечным числом.");
            return value.Number;
        }
        var color = table.Get("color"); var clip = table.Get("clip_children"); var order = Number("order");
        if (color.Type is not (DataType.Nil or DataType.Void or DataType.String) || clip.Type is not (DataType.Nil or DataType.Void or DataType.Boolean) ||
            order is { } n && (Math.Abs(n) > 100000 || n != Math.Truncate(n))) throw new ScriptRuntimeException("Неверное свойство геометрии.");
        return new() { X = Number("x"), Y = Number("y"), Width = Number("width"), Height = Number("height"),
            AnchorX = Number("anchor_x"), AnchorY = Number("anchor_y"), PivotX = Number("pivot_x"), PivotY = Number("pivot_y"),
            ScaleX = Number("scale_x"), ScaleY = Number("scale_y"), Rotation = Number("rotation"), Opacity = Number("opacity"),
            Color = color.IsNil() ? null : color.String, Order = order is null ? null : (int)order.Value, ClipChildren = clip.IsNil() ? null : clip.Boolean };
    }

    private DynValue Execute(DynValue function, params DynValue[] arguments)
    {
        var coroutine = script.CreateCoroutine(function).Coroutine;
        coroutine.AutoYieldCounter = LuaActionScript.InstructionBudget;
        var result = coroutine.Resume(arguments);
        if (result.Type == DataType.YieldRequest || coroutine.State != CoroutineState.Dead)
            throw new InvalidOperationException("Превышен бюджет 20000 инструкций экранного Lua.");
        return result;
    }
}

internal sealed class ScreenLuaWorkerService
{
    private sealed record Entry(ScreenLuaStamp Stamp, Guid ResourceId, ScreenLuaVm Vm) { internal long Version = 1; }
    private readonly Dictionary<Guid, Entry> entries = [];
    private (Guid SessionId, long Generation)? identity;

    internal ScreenLuaResponse Process(ScreenLuaRequest request)
    {
        try { return ProcessCore(request); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InterpreterException)
        {
            var text = error is InterpreterException interpreter ? interpreter.DecoratedMessage : error.Message;
            return new(ScreenLuaProtocol.Version, "error", request.Stamp, text.Length > 4096 ? text[..4096] : text, []);
        }
    }

    private ScreenLuaResponse ProcessCore(ScreenLuaRequest request)
    {
        var stamp = request.Stamp;
        if (request.Protocol != ScreenLuaProtocol.Version || stamp.RequestId == Guid.Empty || stamp.SessionId == Guid.Empty || stamp.Generation < 1 || stamp.Version < 0 ||
            request.Nodes.IsDefault || !LuaLimits.ValidVariables(request.Snapshot) || !LuaActionScript.ValidReferences(request.References))
            throw new ArgumentException("Неверная версия или данные протокола экрана.");
        ScreenLuaResponse Reply(string kind, ImmutableArray<ScreenChange> changes) => new(ScreenLuaProtocol.Version, kind, stamp, null, changes);
        if (request.Kind == "handshake")
        {
            if (identity is not null || stamp.OwnerId != Guid.Empty || stamp.InstanceId != Guid.Empty || stamp.Version != 0 ||
                request.ResourceId != Guid.Empty || !request.Nodes.IsEmpty || request.Source is not null || !request.References.IsEmpty || request.Event is not null || !request.Snapshot.IsEmpty)
                throw new ArgumentException("Неверное приветствие экранного worker.");
            identity = (stamp.SessionId, stamp.Generation);
            return Reply("ready", []);
        }
        if (identity != (stamp.SessionId, stamp.Generation) || stamp.OwnerId == Guid.Empty || stamp.InstanceId == Guid.Empty || request.ResourceId == Guid.Empty)
            throw new ArgumentException("Чужой сеанс или экземпляр экрана.");
        if (request.Kind == "load")
        {
            if (stamp.Version != 0 || request.Source is null || request.Event is not null || entries.ContainsKey(stamp.InstanceId) || entries.Count >= ScreenStack.MaxInstances ||
                entries.Values.Sum(e => e.Vm.Definition.Nodes.Length) + request.Nodes.Length > ScreenStack.MaxSessionNodes)
                throw new ArgumentException("Недопустимая загрузка или бюджет экранного worker.");
            var definition = new ScreenDefinition(request.ResourceId, request.Nodes);
            if (request.References.Any(r => !definition.Nodes.Any(n => n.Id == r.Value))) throw new ArgumentException("Чужая именованная ссылка экрана.");
            var vm = new ScreenLuaVm(definition, stamp.InstanceId, request.Source, request.References);
            var changes = vm.Handle(null, request.Snapshot);
            _ = ScreenLuaProtocol.Prepare(definition, changes);
            if (entries.Values.Sum(e => e.Vm.Definition.Nodes.Length) + vm.Definition.Nodes.Length > ScreenStack.MaxSessionNodes)
                throw new InvalidOperationException("Превышен бюджет узлов сеанса.");
            entries.Add(stamp.InstanceId, new(stamp, request.ResourceId, vm));
            return Reply("batch", changes);
        }
        if (!entries.TryGetValue(stamp.InstanceId, out var entry) || entry.Stamp.OwnerId != stamp.OwnerId ||
            entry.Version != stamp.Version || entry.ResourceId != request.ResourceId ||
            !request.Nodes.IsEmpty || request.Source is not null || !request.References.IsEmpty)
            throw new ArgumentException("Устаревшие данные или чужой владелец экрана.");
        if (request.Kind == "close" && request.Event is null && request.Snapshot.IsEmpty)
        { entries.Remove(stamp.InstanceId); return Reply("closed", []); }
        if (request.Kind != "event" || request.Event is null) throw new ArgumentException("Неизвестное сообщение экрана.");
        ScreenLuaProtocol.ValidateEvent(entry.Vm.Definition, request.Event, worker: true);
        try
        {
            var before = entry.Vm.Definition;
            var changes = entry.Vm.Handle(request.Event, request.Snapshot);
            _ = ScreenLuaProtocol.Prepare(before, changes);
            if (entries.Values.Sum(e => e.Vm.Definition.Nodes.Length) > ScreenStack.MaxSessionNodes)
                throw new InvalidOperationException("Превышен бюджет узлов сеанса.");
            entry.Version++;
            return Reply("batch", changes);
        }
        catch { entries.Remove(stamp.InstanceId); throw; }
    }
}

/// <summary>Separate entry point; no MoonSharp parser/VM is invoked by the host API.</summary>
public static class ScreenLuaWorkerEntry
{
    public static int Run()
    {
        try
        {
            var service = new ScreenLuaWorkerService();
            var input = Console.OpenStandardInput(); var output = Console.OpenStandardOutput();
            while (true)
            {
                var request = ScreenLuaProtocol.ReadAsync<ScreenLuaRequest>(input, CancellationToken.None).GetAwaiter().GetResult();
                var response = service.Process(request);
                ScreenLuaProtocol.WriteAsync(output, response, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (EndOfStreamException) { return 0; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { Console.Error.WriteLine(error.Message.Length > 4096 ? error.Message[..4096] : error.Message); return 2; }
    }
}
