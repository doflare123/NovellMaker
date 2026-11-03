using System.Text;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Text;

namespace NovellMaker.Core.Execution;

/// <summary>A bounded, read-only projection of one recorded visit. Indices and pages are one-based.</summary>
public sealed record HistoryPage(int Count, int Index, int Page, int Pages, Guid? VisitId,
    Guid? SceneId, Guid? LineId, string Language, string Speaker, string Text, bool CanReplay)
{
    public const int DefaultPageSize = 700;
    public const int MaximumPageSize = 2048;

    public void Validate()
    {
        if (Count is < 0 or > DialogueHistory.MaximumEntries || Index < 0 || Index > Count ||
            Page < 0 || Pages < 0 || Page > Pages ||
            Language is null || Language.Length > 64 || Speaker is null || Speaker.Length > 256 ||
            Text is null || Text.Length > MaximumPageSize * 2 ||
            (Count == 0 ? Index != 0 || Page != 0 || Pages != 0 || VisitId is not null || SceneId is not null || LineId is not null ||
                Language.Length != 0 || Speaker.Length != 0 || Text.Length != 0 || CanReplay :
                Index == 0 || Page == 0 || VisitId is null || VisitId == Guid.Empty || SceneId is null || SceneId == Guid.Empty || LineId is null || LineId == Guid.Empty))
            throw new ArgumentException("Некорректная страница истории.");
    }

    public static HistoryPage Read(NovelProject project, GameState state, int index, int page, int pageSize,
        Func<Guid, string, bool> canReplay)
    {
        if (index is < 0 or > DialogueHistory.MaximumEntries || page is < 1 or > 1000000 || pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentException("Некорректный запрос истории.");
        if (state.History.IsEmpty) return new(0, 0, 0, 0, null, null, null, "", "", "", false);
        index = index == 0 ? state.History.Length : Math.Clamp(index, 1, state.History.Length);
        var entry = state.History[index - 1];
        var line = project.Scenes.Single(s => s.Id == entry.SceneId).Lines.Single(l => l.Id == entry.LineId);
        var source = line.Texts.GetValueOrDefault(entry.Language) ?? line.Texts.GetValueOrDefault(project.DefaultLanguage, "");
        var text = string.Concat(DialogueText.Parse(source).Select(r => r.Text));
        // Limit explicit line breaks as well as scalars so newline-heavy prose remains reachable.
        var chunks = new List<string>(); var slice = new StringBuilder(); var count = 0; var newlines = 0;
        foreach (var rune in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').EnumerateRunes())
        {
            slice.Append(rune.ToString()); count++;
            if (rune.Value == '\n') newlines++;
            if (count == pageSize || newlines == 8) { chunks.Add(slice.ToString()); slice.Clear(); count = 0; newlines = 0; }
        }
        if (slice.Length > 0 || chunks.Count == 0) chunks.Add(slice.ToString());
        page = Math.Clamp(page, 1, chunks.Count);
        static string Clip(string value, int limit) => value.Length <= limit ? value : value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
        return new(state.History.Length, index, page, chunks.Count, entry.VisitId, entry.SceneId, entry.LineId,
            Clip(entry.Language, 64), Clip(project.Characters.FirstOrDefault(c => c.Id == line.SpeakerId)?.Name ?? "", 256), chunks[page - 1],
            canReplay(entry.LineId, entry.Language));
    }
}
