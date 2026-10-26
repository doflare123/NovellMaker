namespace NovellMaker.Core.Execution;

/// <summary>Iterative preflight plus bounded data accepted from the isolated Lua VM.</summary>
public static class LuaLimits
{
    internal static System.Collections.Immutable.ImmutableDictionary<string, string> SetVariable(
        System.Collections.Immutable.ImmutableDictionary<string, string> variables, ref int characters, string name, string value)
    {
        if (!ValidVariable(name, value)) throw new InvalidOperationException("Превышен бюджет переменных.");
        var exists = variables.TryGetValue(name, out var previous);
        var total = characters + value.Length + (exists ? -previous!.Length : name.Length);
        if ((!exists && variables.Count >= MaximumVariableCount) || total > MaximumVariableCharacters)
            throw new InvalidOperationException("Превышен бюджет переменных.");
        var result = variables.SetItem(name, value);
        characters = total;
        return result;
    }
    public const int MaximumNesting = 64;
    public const int MaximumTokens = 4096;
    public const int MaximumValueLength = 8192;
    public const int MaximumVariableCount = 256;
    public const int MaximumVariableCharacters = 131072;
    public const int MaximumQueueCharacters = 524288;

    public static bool ValidVariable(string name, string value) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && value is not null && value.Length <= MaximumValueLength;
    public static bool ValidVariables(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var count = 0; var length = 0;
        foreach (var pair in variables)
        {
            if (++count > MaximumVariableCount || !ValidVariable(pair.Key, pair.Value)) return false;
            length += pair.Key.Length + pair.Value.Length;
            if (length > MaximumVariableCharacters) return false;
        }
        return true;
    }

    public static bool ValidQueue(IEnumerable<ScenarioAction> commands)
    {
        var length = 0; var count = 0;
        foreach (var command in commands)
        {
            if (++count > 4096 || command is null) return false;
            length += command switch
            {
                SetAction a => Size(a.Variable) + Size(a.Value),
                ScriptAction a => Size(a.Source) + Size(a.Name) + (a.References is null ? MaximumQueueCharacters + 1 : a.References.Keys.Sum(Size)),
                ChoiceAction a => a.Options.IsDefault ? MaximumQueueCharacters + 1 : a.Options.Sum(o => Size(o?.Text)),
                HotspotAction a => Size(a.Label),
                BranchAction a => Size(a.Variable) + Size(a.EqualsValue),
                StartMinigameAction a => a.Parameters?.JsonByteCount ?? MaximumQueueCharacters + 1,
                BranchMinigameAction a => Size(a.Field) + (a.EqualsValue?.JsonByteCount ?? MaximumQueueCharacters + 1),
                WaitAction a => Size(a.Mode),
                _ => 0
            };
            if (length > MaximumQueueCharacters) return false;
        }
        return true;
    }

    private static int Size(string? value) => value?.Length ?? MaximumQueueCharacters + 1;

    public static void CheckSource(string source)
    {
        if (source is null || source.Length > LuaActionScript.MaximumSourceLength) throw new ArgumentException("Превышен размер Lua-скрипта.");
        var nesting = 0; var blocks = 0; var tokens = 0; var operators = 0; var structures = 0;
        for (var i = 0; i < source.Length;)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < source.Length && source[i + 1] == '-')
            {
                i += 2;
                if (!SkipLong(source, ref i)) while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (++tokens > MaximumTokens) throw new ArgumentException("Превышен лимит сложности Lua (4096 токенов).");
            if (c is '\'' or '"')
            {
                i++;
                while (i < source.Length) { var next = source[i++]; if (next == '\\') i = Math.Min(source.Length, i + 1); else if (next == c) break; }
                continue;
            }
            if (c == '[' && SkipLong(source, ref i)) continue;
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                var word = source[start..i];
                if (word is "function" or "if" or "do" or "repeat") { blocks++; structures++; }
                if (word is "end" or "until") blocks = Math.Max(0, blocks - 1);
                if (word is "elseif" or "for" or "while") structures++;
                if (word is "not" or "and" or "or") operators++;
            }
            else
            {
                i++;
                if (c is '(' or '[' or '{') nesting++;
                if (c is ')' or ']' or '}') nesting = Math.Max(0, nesting - 1);
                if ("+-*/%^#~=<>.:".Contains(c)) operators++;
            }
            if (nesting + blocks > MaximumNesting || structures > 128 || operators > 256)
                throw new ArgumentException("Превышен лимит вложенности/сложности Lua (64 уровня, 128 блоков, 256 операторов).");
        }
    }

    private static bool SkipLong(string source, ref int index)
    {
        if (index >= source.Length || source[index] != '[') return false;
        var probe = index + 1;
        while (probe < source.Length && source[probe] == '=') probe++;
        if (probe >= source.Length || source[probe] != '[') return false;
        var equals = probe - index - 1; index = probe + 1;
        while (index < source.Length)
        {
            if (source[index++] != ']') continue;
            probe = index;
            for (var n = 0; n < equals && probe < source.Length && source[probe] == '='; n++) probe++;
            if (probe - index == equals && probe < source.Length && source[probe] == ']') { index = probe + 1; return true; }
        }
        return true;
    }
}
