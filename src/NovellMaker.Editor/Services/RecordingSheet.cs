using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Editor.Services;

public sealed record RecordingRow(int Number, string Speaker, string Text, string Language, Guid LineId, string Wav, bool Required);
public enum RecordingSheetScope { Scene, Filtered }
public sealed record VoiceImportItem(RecordingRow Row, string? Source, string? Hash, Guid? PreviousAssetId, string Status, bool Unchanged = false)
{
    public bool Replaces => PreviousAssetId.HasValue;
    public bool CanImport => Source is not null && !Unchanged;
    public string Label => $"{Row.Number}. {Row.Speaker} · {Row.Language} · {Row.Wav} — {Status}";
}
public sealed record VoiceImportPlan(Guid ProjectId, Guid SceneId, IReadOnlyList<VoiceImportItem> Items, int ExtraFiles)
{
    public int Matches => Items.Count(i => i.Source is not null);
    public int ImportCount => Items.Count(i => i.CanImport);
    public int UnchangedCount => Items.Count(i => i.Unchanged);
    public int Replacements => Items.Count(i => i.CanImport && i.Replaces);
    public int Missing => Items.Count(i => i.Status == "Файл отсутствует");
    public string Summary => $"{Matches} совпадений, {UnchangedCount} без изменений, {Missing} файлов отсутствуют, {Replacements} записей будут заменены. " +
        $"Пропущено строк: {Items.Count - Matches - Missing}; файлов вне листа: {ExtraFiles}.";
}

public static class RecordingSheet
{
    private static readonly string[] Headers = ["Number", "Speaker", "Text", "Language", "LineId", "Wav", "Required"];
    public static string SuggestedWav(Guid id, string language) => $"{id:N}_{new string(language.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray())}.wav";
    public static IReadOnlyList<RecordingRow> Rows(NovelProject project, NovelScene scene, string language) => scene.Lines.Select((line, i) =>
        new RecordingRow(i + 1, project.Characters.FirstOrDefault(c => c.Id == line.SpeakerId)?.Name ?? "Рассказчик",
            line.Texts.GetValueOrDefault(language, ""), language, line.Id, SuggestedWav(line.Id, language),
            project.Audio.VoiceOmissions.GetValueOrDefault(line.Id)?.Contains(language) != true)).ToArray();

    public static string Write(IEnumerable<RecordingRow> rows, char separator)
    {
        if (separator is not (',' or '\t')) throw new ArgumentException("Разделитель CSV или TSV.");
        string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var result = new StringBuilder().AppendLine(string.Join(separator, Headers));
        foreach (var row in rows)
            result.AppendLine(string.Join(separator, new[] { row.Number.ToString(CultureInfo.InvariantCulture), row.Speaker, row.Text, row.Language,
                row.LineId.ToString("D"), row.Wav, row.Required ? "true" : "false" }.Select(Escape)));
        return result.ToString();
    }
    public static IReadOnlyList<RecordingRow> Read(string content)
    {
        content = content.TrimStart('\uFEFF');
        var separator = content.TakeWhile(c => c is not ('\r' or '\n')).Contains('\t') ? '\t' : ',';
        var records = DelimitedText.Read(content, separator);
        if (records.Count == 0 || !records[0].SequenceEqual(Headers)) throw new InvalidDataException("Неверный заголовок листа записи. Используйте экспорт CSV/TSV из редактора.");
        var result = new List<RecordingRow>();
        var keys = new HashSet<(Guid, string)>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var values in records.Skip(1))
        {
            if (values.Length != Headers.Length || !int.TryParse(values[0], out var number) || number < 1 ||
                !Guid.TryParse(values[4], out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(values[3]) || !bool.TryParse(values[6], out var required))
                throw new InvalidDataException($"Некорректная строка листа записи: {result.Count + 2}.");
            var name = values[5];
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name != Path.GetFileName(name) || !name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("В колонке Wav нужно имя WAV без пути.");
            if (!keys.Add((id, values[3])) || !names.Add(name)) throw new InvalidDataException("Повторный ID/язык или имя WAV в листе записи.");
            result.Add(new(number, values[1], values[2], values[3], id, name, required));
        }
        return result;
    }
    public static async Task<VoiceImportPlan> PreviewAsync(NovelProject project, Guid sceneId, IReadOnlyList<RecordingRow> rows, string folder, string? projectDirectory = null)
    {
        var scene = project.Scenes.Single(s => s.Id == sceneId);
        var files = Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key!, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var items = new List<VoiceImportItem>();
        foreach (var row in rows)
        {
            var line = scene.Lines.FirstOrDefault(l => l.Id == row.LineId);
            Guid? previous = project.Audio.Voices.GetValueOrDefault(row.LineId)?.TryGetValue(row.Language, out var existing) == true ? existing : null;
            string? source = null, hash = null; string status; var unchanged = false;
            if (line is null) status = "ID фразы отсутствует в этой сцене";
            else if (!line.Texts.ContainsKey(row.Language)) status = "У фразы нет текста на этом языке";
            else if (!row.Required || project.Audio.VoiceOmissions.GetValueOrDefault(row.LineId)?.Contains(row.Language) == true) status = "Без озвучки — пропущено";
            else if (!files.TryGetValue(row.Wav, out var candidates)) status = "Файл отсутствует";
            else if (candidates.Length != 1) status = "Неоднозначное имя файла";
            else
            {
                try
                {
                    using var input = File.OpenRead(candidates[0]); var wave = PcmWave.Inspect(input);
                    input.Position = 0; hash = Convert.ToHexString(await SHA256.HashDataAsync(input)); source = candidates[0];
                    unchanged = await AssetMatchesAsync(project.Audio, previous, projectDirectory, hash);
                    status = (unchanged ? "Без изменений" : previous.HasValue ? "Заменить" : "Прикрепить") + $" · {wave.Length / (double)(wave.SampleRate * wave.Channels * 2):0.0} с" +
                        (line.Texts[row.Language] != row.Text ? " · текст изменён после экспорта" : "");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { status = "Ошибка WAV: " + error.Message; }
            }
            items.Add(new(row, source, hash, previous, status, unchanged));
        }
        var expected = rows.Select(r => r.Wav).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(project.Id, sceneId, items, files.Where(p => !expected.Contains(p.Key)).Sum(p => p.Value.Length));
    }

    internal static async Task<bool> AssetMatchesAsync(AudioProject audio, Guid? assetId, string? projectDirectory, string? hash)
    {
        if (projectDirectory is null || hash is null || audio.Assets.FirstOrDefault(a => a.Id == assetId) is not { } asset) return false;
        try
        {
            using var stored = File.OpenRead(AudioAssetPath.Resolve(projectDirectory, asset.RelativePath));
            return Convert.ToHexString(await SHA256.HashDataAsync(stored)) == hash;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
