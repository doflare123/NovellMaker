using MoonSharp.Interpreter;

namespace NovellMaker.Core.Scripting;

/// <summary>VM-local opt-in validation; legacy coercions and ignored extra arguments remain the default.</summary>
internal sealed class LuaApiArguments(string scope)
{
    private bool strict;

    internal void CheckCount(string name, CallbackArguments args, int minimum, int maximum)
    {
        if (strict && (args.Count < minimum || args.Count > maximum))
            throw new ScriptRuntimeException($"{scope}.{name}: неверное количество аргументов (ожидается {minimum}..{maximum}).");
    }

    internal DynValue Read(CallbackArguments args, int index, DataType type)
    {
        if (strict && args[index].Type != type)
            throw new ScriptRuntimeException($"{scope}: аргумент {index + 1} должен иметь тип {type}.");
        return args.AsType(index, scope, type, false);
    }

    internal DynValue Configure(CallbackArguments args)
    {
        // The switch itself always has an exact signature, even in compatibility mode.
        if (args.Count != 1 || args[0].Type != DataType.Boolean)
            throw new ScriptRuntimeException($"{scope}.strict_arguments ожидает один boolean.");
        strict = args[0].Boolean;
        return DynValue.Nil;
    }
}
