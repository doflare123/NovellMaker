using System.Reflection;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace NovellMaker.Core.Scripting;

/// <summary>Worker-only adapter for pinned MoonSharp 2.0.0. No reflection is exposed to Lua.</summary>
internal static class LuaCallingLocation
{
    private static readonly FieldInfo Processor = typeof(Coroutine).GetField("m_Processor", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo SourceAt = Processor.FieldType.GetMethod("GetCurrentSourceRef", BindingFlags.NonPublic | BindingFlags.Instance)!;

    internal static SourceRef? Read(ScriptExecutionContext context)
    {
        if (context.CallingLocation is { IsClrLocation: false } direct) return direct;
        var coroutine = context.GetCallingCoroutine();
        var frame = coroutine.GetStackTrace(0, null).FirstOrDefault();
        // 2.0.0 queries the instruction *after* CALL, often an unmarked POP. The public
        // stack supplies its return address; resolve the preceding CALL without stepping,
        // attaching a debugger, rewriting author source or allocating watches per instruction.
        return frame is { RetAddress: > 0 } ? SourceAt.Invoke(Processor.GetValue(coroutine), [frame.RetAddress - 1]) as SourceRef : null;
    }
}
