using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Execution;

namespace NovellMaker.Core.Screens.Lua;

/// <summary>Only the isolated --screen-lua-worker process uses this VM.</summary>
internal sealed class ScreenLuaVm
{
    private readonly Script script = new(CoreModules.None);
    private readonly DynValue handlers;
    private ImmutableArray<ScreenChange>.Builder changes = ImmutableArray.CreateBuilder<ScreenChange>();
    private ImmutableDictionary<string, string> snapshot = ImmutableDictionary<string, string>.Empty;
    internal ScreenDefinition Definition { get; private set; }

    internal ScreenLuaVm(ScreenDefinition definition, string source, ImmutableDictionary<string, Guid> references)
    {
        Definition = definition;
        LuaLimits.CheckSource(source);
        var ui = new Table(script);
        var refs = new Table(script);
        foreach (var pair in references) refs.Set(pair.Key, DynValue.NewString(pair.Value.ToString()));
        script.Globals.Set("refs", DynValue.NewTable(refs));
        void Bind(string name, Func<CallbackArguments, DynValue> callback) => ui.Set(name, DynValue.NewCallback((_, args) => callback(args)));
        string Str(CallbackArguments args, int index) => args.AsType(index, "ui", DataType.String, false).String;
        Guid Node(CallbackArguments args) => Guid.TryParse(Str(args, 0), out var id) && definition.Nodes.Any(n => n.Id == id)
            ? id : throw new ScriptRuntimeException("Чужой узел экрана.");
        DynValue Add(ScreenChange change)
        {
            if (changes.Count >= LuaActionScript.MaximumCommands) throw new ScriptRuntimeException("Превышен лимит изменений экрана.");
            changes.Add(change); return DynValue.Nil;
        }
        Bind("set_text", a => Add(new("text", Node(a), Str(a, 1), null)));
        Bind("set_visible", a => Add(new("visible", Node(a), null, a.AsType(1, "ui", DataType.Boolean, false).Boolean)));
        Bind("set_enabled", a => Add(new("enabled", Node(a), null, a.AsType(1, "ui", DataType.Boolean, false).Boolean)));
        Bind("close", _ => Add(new("close", null, null, null)));
        Bind("get", a => snapshot.TryGetValue(Str(a, 0), out var value) ? DynValue.NewString(value) : DynValue.Nil);
        script.Globals.Set("ui", DynValue.NewTable(ui));
        handlers = Execute(script.LoadString(source, null, "screen.lua"));
        if (handlers.Type != DataType.Table || handlers.Table.Get("on_event").Type != DataType.Function ||
            handlers.Table.Get("on_load").Type is not (DataType.Nil or DataType.Function))
            throw new InvalidOperationException("Экранный Lua должен вернуть таблицу с функцией on_event и необязательной on_load.");
        if (changes.Count != 0) throw new InvalidOperationException("Изменения экрана разрешены только в обработчике.");
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
            argument.Set("node", value.NodeId is { } node ? DynValue.NewString(node.ToString()) : DynValue.Nil);
            argument.Set("x", DynValue.NewNumber(value.X)); argument.Set("y", DynValue.NewNumber(value.Y));
            argument.Set("seconds", DynValue.NewNumber(value.Seconds));
        }
        if (function.Type != DataType.Nil) Execute(function, DynValue.NewTable(argument));
        var batch = changes.ToImmutable();
        Definition = ScreenLuaProtocol.Prepare(Definition, batch).Definition;
        return batch;
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
            var vm = new ScreenLuaVm(definition, request.Source, request.References);
            var changes = vm.Handle(null, request.Snapshot);
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
        ScreenLuaProtocol.ValidateEvent(entry.Vm.Definition, request.Event);
        try
        {
            var changes = entry.Vm.Handle(request.Event, request.Snapshot);
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
