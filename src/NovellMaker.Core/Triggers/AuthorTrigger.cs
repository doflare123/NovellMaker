namespace NovellMaker.Core.Triggers;

/// <summary>A transient named event. It does not store flags or impose gameplay semantics.</summary>
public sealed record AuthorTrigger(string Id, string? Data = null)
{
    public const int MaximumDataCharacters = 1024;
    public const int MaximumPerBatch = 16;
    public const int MaximumChainDepth = 32;
    public void Validate()
    {
        if (!AuthorKey.IsValid(Id) || Data is { Length: > MaximumDataCharacters })
            throw new ArgumentException("Некорректный ID или данные триггера.");
    }
}
