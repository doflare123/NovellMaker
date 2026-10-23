namespace NovellMaker.Core;

/// <summary>Stable author-defined names, independent of captions, files and runtime handles.</summary>
public static class AuthorKey
{
    public static bool IsValid(string? key) => key is { Length: >= 1 and <= 64 } &&
        key[0] is >= 'a' and <= 'z' && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');
}
