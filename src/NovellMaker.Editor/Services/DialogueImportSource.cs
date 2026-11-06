using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovellMaker.Core.Projects;

namespace NovellMaker.Editor.Services;

public sealed record ImportPose(string Key, string Name, string Image);
public sealed record ImportCharacter(string Key, string Name)
{
    public ImmutableArray<ImportPose> Poses { get; init; } = [];
    public string? DefaultPose { get; init; }
}
public sealed record ImportLine
{
    public string? Key { get; init; }
    public string? Speaker { get; init; }
    public string? Pose { get; init; }
    public string? Text { get; init; }
    public string? Voice { get; init; }
    public string? Language { get; init; }
    public ImmutableDictionary<string, string> Texts { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableDictionary<string, string> Voices { get; init; } = ImmutableDictionary<string, string>.Empty;
    public ImmutableHashSet<string> Unvoiced { get; init; } = [];
}
public sealed record ImportScene
{
    public string? Key { get; init; }
    public string Name { get; init; } = "";
    public ImmutableArray<ImportLine> Lines { get; init; } = [];
}
public sealed record DialogueImportSource
{
    [JsonRequired] public int Version { get; init; } = 1;
    public string? Language { get; init; }
    public ImmutableArray<ImportCharacter> Characters { get; init; } = [];
    public ImmutableArray<ImportScene> Scenes { get; init; } = [];
    public ImmutableArray<ImportLine> Lines { get; init; } = [];
}
public sealed record ImportTable(string[] Headers, IReadOnlyList<string[]> Rows);

public static class DialogueImportReader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 32 };
    public static DialogueImportSource ReadJson(string content) => JsonSerializer.Deserialize<DialogueImportSource>(content.TrimStart('\uFEFF'), Json)
        ?? throw new InvalidDataException("JSON импорта пуст.");
    public static ImportTable ReadTable(string content, string separator)
    {
        if (separator is not ("," or ";" or "\t")) throw new InvalidDataException("Выберите запятую, точку с запятой или табуляцию.");
        var records = DelimitedText.Read(content.TrimStart('\uFEFF'), separator[0]);
        if (records.Count < 1) throw new InvalidDataException("Таблица пуста.");
        var headers = records[0].Select(h => h.Trim()).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace) || headers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Length)
            throw new InvalidDataException("Заголовки колонок должны быть непустыми и уникальными.");
        if (records.Skip(1).Any(r => r.Length != headers.Length)) throw new InvalidDataException("Количество полей строки не совпадает с заголовком. Проверьте разделитель.");
        if (records.Count > 20001) throw new InvalidDataException("В одном импорте допускается до 20000 строк.");
        return new(headers, records.Skip(1).ToArray());
    }
    public static string DetectSeparator(string content, string path)
    {
        if (Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase)) return "\t";
        var first = content.Split('\n')[0];
        return first.Contains('\t') ? "\t" : first.Count(c => c == ';') > first.Count(c => c == ',') ? ";" : ",";
    }
    public static ImmutableDictionary<string, string> SuggestColumns(ImportTable table)
    {
        var aliases = new Dictionary<string, string[]> {
            ["text"] = ["text", "текст", "реплика"], ["speaker"] = ["speaker", "персонаж", "говорящий"],
            ["characterKey"] = ["characterkey", "character_id", "ключ персонажа"], ["lineKey"] = ["linekey", "lineid", "id", "ключ реплики"],
            ["language"] = ["language", "язык"], ["voice"] = ["voice", "wav", "запись", "озвучка"],
            ["image"] = ["image", "изображение", "спрайт"], ["pose"] = ["pose", "поза"],
            ["scene"] = ["scene", "сцена"], ["sceneKey"] = ["scenekey", "scene_id", "ключ сцены"] };
        return aliases.Select(p => (p.Key, Header: table.Headers.FirstOrDefault(h => p.Value.Contains(h, StringComparer.OrdinalIgnoreCase))))
            .Where(p => p.Header is not null).ToImmutableDictionary(p => p.Key, p => p.Header!);
    }
    public static DialogueImportSource FromTable(ImportTable table, DialogueImportProfile profile)
    {
        if (!profile.Columns.ContainsKey("text") && !profile.Columns.ContainsKey("speaker")) throw new InvalidDataException("Назначьте колонку текста или персонажа.");
        foreach (var column in profile.Columns)
            if (!AuthoringImports.ColumnFields.Contains(column.Key) || !table.Headers.Contains(column.Value)) throw new InvalidDataException("Колонка профиля отсутствует: " + column.Value);
        string Cell(string[] row, string field) => profile.Columns.TryGetValue(field, out var header) ? row[Array.IndexOf(table.Headers, header)] : "";
        var characters = new Dictionary<string, ImportCharacter>(StringComparer.Ordinal);
        var scenes = new Dictionary<string, (string Name, List<ImportLine> Lines)>();
        foreach (var row in table.Rows)
        {
            var name = Cell(row, "speaker").Trim(); var characterKey = Cell(row, "characterKey").Trim();
            if (characterKey.Length == 0) characterKey = name;
            var image = Cell(row, "image").Trim(); var pose = Cell(row, "pose").Trim();
            if (characterKey.Length > 0)
            {
                if (name.Length == 0) name = characterKey;
                if (!characters.TryGetValue(characterKey, out var character)) character = new(characterKey, name);
                if (character.Name != name) throw new InvalidDataException("У одного ключа персонажа разные имена: " + characterKey);
                if (image.Length > 0)
                {
                    var poseKey = pose.Length > 0 ? pose : "default";
                    var old = character.Poses.FirstOrDefault(p => p.Key == poseKey);
                    if (old is not null && old.Image != image) throw new InvalidDataException("У одной позы разные изображения: " + characterKey + "/" + poseKey);
                    if (old is null) character = character with { Poses = character.Poses.Add(new(poseKey, poseKey, image)) };
                }
                characters[characterKey] = character;
            }
            if (!profile.Columns.ContainsKey("text")) continue;
            var language = Cell(row, "language").Trim(); if (language.Length == 0) language = profile.Language;
            var voice = Cell(row, "voice").Trim(); var key = Cell(row, "lineKey").Trim();
            var sceneKey = Cell(row, "sceneKey").Trim(); var sceneName = Cell(row, "scene").Trim();
            if (sceneKey.Length == 0) sceneKey = sceneName;
            if (!scenes.TryGetValue(sceneKey, out var group)) group = (sceneName, []);
            if (group.Name != sceneName) throw new InvalidDataException("У одного ключа сцены разные имена.");
            var line = new ImportLine { Key = key.Length == 0 ? null : key, Speaker = characterKey.Length == 0 ? null : characterKey,
                Pose = pose.Length == 0 ? null : pose, Texts = ImmutableDictionary<string, string>.Empty.Add(language, Cell(row, "text")),
                Voices = voice.Length == 0 ? ImmutableDictionary<string, string>.Empty : ImmutableDictionary<string, string>.Empty.Add(language, voice) };
            var at = key.Length == 0 ? -1 : group.Lines.FindIndex(l => l.Key == key);
            if (at < 0) group.Lines.Add(line);
            else
            {
                var previous = group.Lines[at];
                if (previous.Texts.ContainsKey(language) || previous.Speaker != line.Speaker || previous.Pose != line.Pose) throw new InvalidDataException("Повторный ключ/язык или разные персонажи у фразы: " + key);
                group.Lines[at] = previous with { Texts = previous.Texts.AddRange(line.Texts), Voices = previous.Voices.AddRange(line.Voices) };
            }
            scenes[sceneKey] = group;
        }
        return new() { Characters = characters.Values.ToImmutableArray(), Scenes = scenes.Select(p => new ImportScene {
            Key = p.Key.Length == 0 ? null : p.Key, Name = p.Value.Name, Lines = p.Value.Lines.ToImmutableArray() }).ToImmutableArray() };
    }
}
