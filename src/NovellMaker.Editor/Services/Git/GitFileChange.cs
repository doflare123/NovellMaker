namespace NovellMaker.Editor.Services.Git;

public sealed record GitFileChange(
    string Path,
    string IndexStatus,
    string WorkTreeStatus,
    string? OriginalPath = null)
{
    public string DisplayStatus
    {
        get
        {
            if (IndexStatus == "?" && WorkTreeStatus == "?")
            {
                return "Неотслеживаемый";
            }

            if (IsConflict(IndexStatus, WorkTreeStatus))
            {
                return "Конфликт";
            }

            if (IndexStatus != " " && WorkTreeStatus != " ")
            {
                return $"Индекс: {Describe(IndexStatus)}, рабочая копия: {Describe(WorkTreeStatus)}";
            }

            if (IndexStatus != " ")
            {
                return $"В индексе: {Describe(IndexStatus)}";
            }

            return Describe(WorkTreeStatus);
        }
    }

    private static bool IsConflict(string indexStatus, string workTreeStatus)
    {
        var pair = indexStatus + workTreeStatus;
        return pair is "DD" or "AU" or "UD" or "UA" or "DU" or "AA" or "UU";
    }

    private static string Describe(string status) => status switch
    {
        "M" => "изменён",
        "A" => "добавлен",
        "D" => "удалён",
        "R" => "переименован",
        "C" => "скопирован",
        "U" => "конфликт",
        "?" => "неотслеживаемый",
        _ => status,
    };
}
