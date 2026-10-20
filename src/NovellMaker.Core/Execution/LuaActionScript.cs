using System.Collections.Immutable;
using MoonSharp.Interpreter;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Execution;

/// <summary>Bounded, transactional Lua command producer. Every invocation has a fresh VM.</summary>
public static class LuaActionScript
{
    public const int MaximumSourceLength = 32768;
    public const int InstructionBudget = 20000;
    public const int MaximumCommands = 256;

    internal static bool ValidReferences(ImmutableDictionary<string, Guid> references) => references.Count <= 256 && references.All(r =>
        r.Key.Length <= 128 && System.Text.RegularExpressions.Regex.IsMatch(r.Key, "^[A-Za-z_][A-Za-z0-9_]*$") && r.Value != Guid.Empty);

    /// <summary>Parse only: validation never executes project code.</summary>
    public static void ValidateSyntax(string source, string name)
        => ValidateSyntaxAsync(source, name).GetAwaiter().GetResult();

    public static async Task ValidateSyntaxAsync(string source, string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LuaLimits.CheckSource(source);
        var response = await LuaWorkerClient.InvokeAsync(new("syntax", source, name), cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) throw new ArgumentException(response.Error);
    }

    internal static void ValidateInWorker(string source, string name)
    {
        LuaLimits.CheckSource(source);
        try { new Script(CoreModules.None).LoadString(source, null, name); }
        catch (InterpreterException error) { throw new ArgumentException(error.DecoratedMessage, error); }
    }

    public static ImmutableArray<ScenarioAction> Run(ScriptAction action, GameState state, NovelProject project)
        => RunAsync(action, state, project).GetAwaiter().GetResult();

    public static async Task<ImmutableArray<ScenarioAction>> RunAsync(ScriptAction action, GameState state, NovelProject project, CancellationToken cancellationToken = default)
    {
        var commands = await RequestCommandsAsync(action, state, project, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => ValidateCommands(commands, state, project), cancellationToken).ConfigureAwait(false);
    }

    // Only consumers that validate the complete batch before use may call this raw producer.
    internal static async Task<ImmutableArray<ScenarioAction>> RequestCommandsAsync(ScriptAction action, GameState state, NovelProject project, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { LuaLimits.CheckSource(action.Source); }
        catch (ArgumentException error) { throw new InvalidOperationException(error.Message, error); }
        // Construction runs after admission, off-host and inside the transport's original deadline.
        var result = await LuaWorkerClient.InvokePreparedAsync(preparation => new LuaWorkerRequest("run", action.Source, action.Name, action,
            LuaStateSnapshot.Capture(state), LuaCommandCatalog.Capture(project, state.SceneId, preparation)), cancellationToken).ConfigureAwait(false);
        if (result.Error is not null) throw new InvalidOperationException($"Lua «{action.Name}» ({action.Id}): {result.Error}");
        return result.Commands;
    }

    internal static ImmutableArray<ScenarioAction> ValidateCommands(ImmutableArray<ScenarioAction> commands, GameState state, NovelProject project)
    {
        var scene = project.Scenes.First(s => s.Id == state.SceneId);
        if (commands.IsDefault || commands.Length > MaximumCommands || commands.Any(a => a is null || a.Id == Guid.Empty || !ScenarioProgram.IsValidAction(project, scene, a)) || commands.Select(a => a.Id).Distinct().Count() != commands.Length)
            throw new InvalidOperationException("Изолированный Lua вернул некорректные команды.");
        var variables = state.Variables;
        if (!LuaLimits.ValidVariables(variables)) throw new InvalidOperationException("Превышен бюджет переменных Lua.");
        var characters = variables.Sum(v => v.Key.Length + v.Value.Length);
        foreach (var command in commands.OfType<SetAction>()) variables = LuaLimits.SetVariable(variables, ref characters, command.Variable, command.Value);
        return commands;
    }

    internal static ImmutableArray<ScenarioAction> RunInWorker(ScriptAction action, LuaStateSnapshot state, LuaCommandCatalog catalog)
    {
        LuaLimits.CheckSource(action.Source);
        // No libraries with non-preemptible native loops, filesystem, loading or CLR interop.
        var script = new Script(CoreModules.None);
        var api = new Table(script);
        var commands = ImmutableArray.CreateBuilder<ScenarioAction>();
        var variables = state.Variables.ToBuilder();
        void Add(ScenarioAction command)
        {
            if (commands.Count >= MaximumCommands) throw new ScriptRuntimeException("Превышен лимит команд Lua.");
            if (!catalog.IsValid(command)) throw new ScriptRuntimeException($"Некорректные параметры или ссылки команды {command.GetType().Name}.");
            commands.Add(command);
        }
        string Str(CallbackArguments args, int index) => args.AsType(index, "game", DataType.String, false).String;
        double Num(CallbackArguments args, int index)
        {
            var value = args.AsType(index, "game", DataType.Number, false).Number;
            return double.IsFinite(value) ? value : throw new ScriptRuntimeException("Ожидается конечное число.");
        }
        int Layer(CallbackArguments args, int index)
        {
            var value = Num(args, index);
            return value is >= -1000 and <= 1000 && value == Math.Truncate(value) ? (int)value : throw new ScriptRuntimeException("Слой: целое число от -1000 до 1000.");
        }
        Guid Id(CallbackArguments args, int index) => Guid.TryParse(Str(args, index), out var id) && id != Guid.Empty
            ? id : throw new ScriptRuntimeException("Ожидается непустой GUID.");
        void Bind(string name, Func<CallbackArguments, DynValue> callback) =>
            api.Set(name, DynValue.NewCallback((_, args) => callback(args)));
        DynValue Emit(ScenarioAction command) { Add(command); return DynValue.Nil; }
        Bind("get", a => variables.TryGetValue(Str(a, 0), out var value) ? DynValue.NewString(value) : DynValue.Nil);
        Bind("set", a => { var key = Str(a, 0); var value = Str(a, 1);
            if (!LuaLimits.ValidVariable(key, value)) throw new ScriptRuntimeException("Превышен размер переменной Lua.");
            variables[key] = value;
            if (!LuaLimits.ValidVariables(variables)) throw new ScriptRuntimeException("Превышен бюджет переменных Lua.");
            return Emit(new SetAction(Guid.NewGuid(), key, value)); });
        Bind("time", _ => DynValue.NewNumber(state.Time));
        Bind("visible", a => DynValue.NewBoolean(state.VisibleObjects.Contains(Id(a, 0))));
        Bind("object", a =>
        {
            var id = Id(a, 0); var table = new Table(script);
            table.Set("visible", DynValue.NewBoolean(state.VisibleObjects.Contains(id)));
            table.Set("state", state.ObjectStates.TryGetValue(id, out var pose) ? DynValue.NewString(pose.ToString()) : DynValue.Nil);
            if (state.Placements.TryGetValue(id, out var placement))
            {
                table.Set("x", DynValue.NewNumber(placement.X)); table.Set("y", DynValue.NewNumber(placement.Y));
                table.Set("scale", DynValue.NewNumber(placement.Scale)); table.Set("layer", DynValue.NewNumber(placement.Layer));
            }
            return DynValue.NewTable(table);
        });
        Bind("show", a => Emit(new ShowAction(Guid.NewGuid(), Id(a, 0))));
        Bind("hide", a => Emit(new HideAction(Guid.NewGuid(), Id(a, 0))));
        Bind("state", a => Emit(new StateAction(Guid.NewGuid(), Id(a, 0), Id(a, 1))));
        Bind("text", a => Emit(new TextAction(Guid.NewGuid(), Id(a, 0))));
        Bind("place", a => Emit(new PlaceAction(Guid.NewGuid(), Id(a, 0), new CharacterPlacement(Num(a, 1), Num(a, 2), Num(a, 3), Layer(a, 4)))));
        Bind("auto", a => Emit(new PlaceAction(Guid.NewGuid(), Id(a, 0), null)));
        Bind("move", a => Emit(new MoveAction(Guid.NewGuid(), Id(a, 0),
            new CharacterPlacement(Num(a, 1), Num(a, 2), Num(a, 3), Layer(a, 4)), Num(a, 5))));
        Bind("wait", a => Emit(new WaitAction(Guid.NewGuid(), "timer", Num(a, 0))));
        Bind("wait_input", _ => Emit(new WaitAction(Guid.NewGuid(), "input")));
        Bind("wait_actions", _ => Emit(new WaitAction(Guid.NewGuid(), "actions")));
        Bind("wait_click", a => Emit(new WaitAction(Guid.NewGuid(), "interaction", Token: a.Count == 0 ? null : Id(a, 0))));
        Bind("hotspot", a => Emit(new HotspotAction(Guid.NewGuid(), Id(a, 0), Str(a, 1), Num(a, 2), Num(a, 3), Num(a, 4), Num(a, 5))));
        Bind("finish", a => Emit(new SetAction(Guid.NewGuid(), "minigame.result", Str(a, 0))));
        Bind("run", a => Emit(new RunScriptAction(Guid.NewGuid(), Id(a, 0))));
        Bind("jump", a => Emit(new JumpAction(Guid.NewGuid(), Id(a, 0), Id(a, 1))));
        Bind("choice", a =>
        {
            var table = a.AsType(0, "choice", DataType.Table, false).Table;
            if (table.Length is < 1 or > 8) throw new ScriptRuntimeException("Выбор содержит от 1 до 8 вариантов.");
            var options = ImmutableArray.CreateBuilder<ChoiceOption>();
            for (var i = 1; i <= table.Length; i++)
            {
                var option = table.Get(i);
                if (option.Type != DataType.Table) throw new ScriptRuntimeException("Вариант выбора должен быть таблицей.");
                string Field(string name) => option.Table.Get(name).Type == DataType.String ? option.Table.Get(name).String : throw new ScriptRuntimeException("Поле выбора должно быть строкой: " + name);
                Guid Reference(string name) => Guid.TryParse(Field(name), out var id) && id != Guid.Empty ? id : throw new ScriptRuntimeException("Поле выбора должно быть GUID: " + name);
                options.Add(new(Reference("id"), Field("text"), Reference("scene"), Reference("action")));
            }
            return Emit(new ChoiceAction(Guid.NewGuid(), options.ToImmutable()));
        });
        script.Globals.Set("game", DynValue.NewTable(api));
        var references = new Table(script);
        foreach (var reference in action.References) references.Set(reference.Key, DynValue.NewString(reference.Value.ToString()));
        script.Globals.Set("refs", DynValue.NewTable(references));
        Bind("jump_line", a => Emit(new JumpAction(Guid.NewGuid(), Id(a, 0), ScenarioProgram.LineEntryId(Id(a, 1)))));
        try
        {
            var function = script.LoadString(action.Source, null, action.Name);
            var coroutine = script.CreateCoroutine(function).Coroutine;
            coroutine.AutoYieldCounter = InstructionBudget;
            var result = coroutine.Resume();
            if (result.Type == DataType.YieldRequest || coroutine.State != CoroutineState.Dead)
                throw new InvalidOperationException($"Lua «{action.Name}»: превышен бюджет {InstructionBudget} инструкций.");
            return commands.ToImmutable();
        }
        catch (InterpreterException error)
        { throw new InvalidOperationException($"Lua «{action.Name}» ({action.Id}): {error.DecoratedMessage}", error); }
    }
}
