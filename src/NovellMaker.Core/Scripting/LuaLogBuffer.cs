using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace NovellMaker.Core.Scripting;

/// <summary>One bounded invocation, shared by the root and all imported modules.</summary>
internal sealed class LuaLogBuffer
{
    private readonly List<LuaLogMessage> messages = [];
    private int characters;
    internal int Dropped { get; private set; }
    internal ImmutableArray<LuaLogMessage> Messages => [.. messages];
    internal void Write(Script script, ScriptExecutionContext context, CallbackArguments args)
    {
        if (args.Count != 2 || args[0].Type != DataType.String || args[1].Type != DataType.String)
            throw new ScriptRuntimeException("ui.log ожидает уровень и сообщение — две строки.");
        var message = new LuaLogMessage(args[0].String, args[1].String);
        try { message.Validate(); }
        catch (ArgumentException error) { throw new ScriptRuntimeException(error.Message); }
        if (messages.Count >= LuaDiagnosticJournal.MaximumBatchEntries || characters + message.Message.Length > LuaDiagnosticJournal.MaximumBatchCharacters)
        { if (Dropped < int.MaxValue) Dropped++; return; }
        var location = LuaCallingLocation.Read(context);
        var source = location is null ? null : script.GetSourceCode(location.SourceIdx).Name;
        message = message with { ScriptId = ScriptId(source), Line = location?.FromLine };
        characters += message.Message.Length;
        messages.Add(message);
    }

    internal static Guid? ScriptId(string? source) => source is not null && source.StartsWith("Scripts/", StringComparison.Ordinal) &&
        source.EndsWith(".lua", StringComparison.Ordinal) && Guid.TryParseExact(source[8..^4], "N", out var id) && id != Guid.Empty ? id : null;

    internal static LuaLogMessage Fault(Exception error)
    {
        var text = error is InterpreterException lua ? lua.DecoratedMessage : error.Message;
        // MoonSharp uses the stable chunk name, never an absolute source path.
        var match = Regex.Match(text, @"^(?:Scripts/(?<id>[0-9a-f]{32})\.lua|screen\.lua):\((?<line>\d+),", RegexOptions.CultureInvariant);
        Guid? id = match.Success && Guid.TryParseExact(match.Groups["id"].Value, "N", out var parsed) ? parsed : null;
        int? line = match.Success && int.TryParse(match.Groups["line"].Value, out var number) && number is > 0 and <= 32769 ? number : null;
        return new("error", Clip(text), id, line);
    }
    internal static string Clip(string text) => text.Length <= LuaLogMessage.MaximumMessageLength ? text :
        text[..(char.IsHighSurrogate(text[LuaLogMessage.MaximumMessageLength - 1]) ? LuaLogMessage.MaximumMessageLength - 1 : LuaLogMessage.MaximumMessageLength)];
}
