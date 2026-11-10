using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace NovellMaker.Core.Execution;

/// <summary>Observes MoonSharp 2.0 conditional jumps inside the isolated game worker.</summary>
internal sealed class LuaBranchDebugger : IDebugger
{
    private const int MaximumSteps = 4096;
    internal const int MaximumEntries = 64;
    private readonly List<string> entries = [];
    private readonly Dictionary<int, SourceCode> sources = [];
    private string[] byteCode = [];
    private DynValue? stackTop;
    private int steps;
    private bool finished;

    internal string[] Entries => entries.ToArray();

    public DebuggerAction GetAction(int instructionPtr, SourceRef source)
    {
        if (!finished && source is not null && instructionPtr >= 0 && instructionPtr < byteCode.Length &&
            byteCode[instructionPtr].StartsWith("JF ", StringComparison.Ordinal) && stackTop is not null &&
            sources.TryGetValue(source.SourceIdx, out var file))
        {
            var expression = file.GetCodeSnippet(source).Trim();
            if (expression.Length > 240) expression = expression[..240] + "…";
            var name = file.Name.Length <= 256 ? file.Name : file.Name[..256] + "…";
            entries.Add($"Lua {name}:{source.FromLine} · {expression} → {(stackTop.CastToBool() ? "true" : "false")}");
        }
        if (!finished && (++steps >= MaximumSteps || entries.Count >= MaximumEntries - 1))
        {
            entries.Add("Достигнут предел трассировки условий этого вызова Lua.");
            finished = true;
        }
        return new() { Action = finished ? DebuggerAction.ActionType.Run : DebuggerAction.ActionType.ByteCodeStepIn };
    }

    public void Update(WatchType type, IEnumerable<WatchItem> items)
    {
        if (type == WatchType.VStack) stackTop = items.FirstOrDefault()?.Value;
    }
    public DebuggerCaps GetDebuggerCaps() => DebuggerCaps.CanDebugSourceCode | DebuggerCaps.CanDebugByteCode;
    public bool IsPauseRequested() => !finished;
    public void SetSourceCode(SourceCode source) => sources[source.SourceID] = source;
    public void SetByteCode(string[] code) => byteCode = code;
    public List<DynamicExpression> GetWatchItems() => [];
    public void SetDebugService(DebugService service) { }
    public bool SignalRuntimeException(ScriptRuntimeException error) => false;
    public void SignalExecutionEnded() { }
    public void RefreshBreakpoints(IEnumerable<SourceRef> sources) { }
}
