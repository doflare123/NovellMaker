using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Execution;

public enum GameSlotStatus { Compatible, ForeignProject, IncompatibleRevision, UnsupportedVersion, Corrupt, Unavailable }
public sealed record GameSlotMetadata(Guid ProjectId, string Revision, DateTimeOffset SavedAtUtc,
    string Title, Guid SceneId, string SceneName, Guid? LineId, string Phrase, double GameTime)
{
    internal bool IsValid => ProjectId != Guid.Empty && SceneId != Guid.Empty && LineId != Guid.Empty &&
        !string.IsNullOrEmpty(Revision) && Revision.Length <= 128 &&
        Title is { Length: <= 128 } && SceneName is { Length: <= 256 } && Phrase is { Length: <= 512 } &&
        SavedAtUtc != default && SavedAtUtc.Offset == TimeSpan.Zero && double.IsFinite(GameTime) && GameTime >= 0;
}
public sealed record GameSlotSnapshot(GameSlotMetadata Metadata, string SaveJson);
public sealed record GameSlotInfo(string Id, GameSlotStatus Status, GameSlotMetadata? Metadata, string? Diagnostic, bool Legacy = false);
public sealed record GameSlotRead(GameSlotInfo Info, GameSlotSnapshot? Snapshot);

public interface IGameSlotStore
{
    Task SaveAsync(string id, GameSlotSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<GameSlotRead> ReadAsync(string id, CancellationToken cancellationToken = default);
    Task<ImmutableArray<GameSlotInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<GameSlotRead?> LatestAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}

/// <summary>Profile-owned storage. All disk access, parsing and project validation run off the host thread.</summary>
public sealed class GameSlotStore : IGameSlotStore
{
    public const int CurrentVersion = 2;
    public const string QuickSlotId = "quicksave";
    public const int MaxSlots = 128;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    private readonly string directory;
    private readonly string quickPath;
    private readonly NovelProject project;
    private readonly string revision;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int admitted;
    private sealed record Document(int Version, GameSlotMetadata Metadata, string SaveJson);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public GameSlotStore(NovelProject project, string directory, string quickSavePath)
    {
        this.project = project ?? throw new ArgumentNullException(nameof(project));
        this.directory = Path.GetFullPath(directory);
        quickPath = Path.GetFullPath(quickSavePath);
        revision = ScenarioProgram.Fingerprint(project);
        // The legacy quickslot can live outside slots/, but never alias a named slot.
        if (string.Equals(Path.GetDirectoryName(quickPath), this.directory, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Быстрый слот должен находиться отдельно от каталога именованных слотов.");
    }

    /// <summary>Call only on the host thread, before starting file I/O. No player escapes into a Task.</summary>
    public static GameSlotSnapshot Capture(GameSession session, string? title = null, DateTimeOffset? savedAt = null)
    {
        if (session.Phase != GameSessionPhase.Playing) throw new InvalidOperationException("Нет активного прохождения.");
        var player = session.Player;
        var json = GameSave.Serialize(player.Execution); // Includes pending-Lua/save-point restrictions.
        var state = player.Execution.State;
        var scene = player.Execution.Project.Scenes.First(s => s.Id == state.SceneId);
        return new(new(state.ProjectId, state.Revision, (savedAt ?? DateTimeOffset.UtcNow).ToUniversalTime(),
            Clip(title ?? scene.Name, 128), state.SceneId, Clip(scene.Name, 256), state.LineId,
            Clip(player.Frame.Text, 512), state.Time), json);
    }

    public Task SaveAsync(string id, GameSlotSnapshot snapshot, CancellationToken cancellationToken = default) =>
        Run<object?>(token =>
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.SaveJson is null || snapshot.SaveJson.Length > MaxFileBytes)
                throw new ArgumentException("Сохранение превышает 16 МиБ или не содержит снимка.");
            var path = SlotPath(id);
            var state = GameSave.ReadState(snapshot.SaveJson);
            ValidateMetadata(snapshot.Metadata, state);
            GameSave.Validate(state, project);
            var bytes = Utf8.GetBytes(JsonSerializer.Serialize(new Document(CurrentVersion, snapshot.Metadata, snapshot.SaveJson), Options));
            if (bytes.Length > MaxFileBytes) throw new ArgumentException("Сохранение превышает 16 МиБ.");
            CheckPath(path);
            if (!File.Exists(path) && Paths(token).Count >= MaxSlots) throw new InvalidOperationException("Достигнут предел 128 слотов.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            CheckPath(path);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    for (var at = 0; at < bytes.Length; at += 65536)
                    { token.ThrowIfCancellationRequested(); file.Write(bytes, at, Math.Min(65536, bytes.Length - at)); }
                    file.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested(); CheckPath(path);
                File.Move(temporary, path, overwrite: true); // Commit point; cancellation after this does not undo a valid save.
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            return null;
        }, cancellationToken);

    public Task<GameSlotRead> ReadAsync(string id, CancellationToken cancellationToken = default) =>
        Run(token => Read(id, SlotPath(id), token), cancellationToken);

    public Task<ImmutableArray<GameSlotInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Run(token => Scan(token), cancellationToken);

    public Task<GameSlotRead?> LatestAsync(CancellationToken cancellationToken = default) =>
        Run<GameSlotRead?>(token =>
        {
            foreach (var info in Scan(token).Where(s => s.Status == GameSlotStatus.Compatible))
            {
                var read = Read(info.Id, SlotPath(info.Id), token);
                if (read.Info.Status == GameSlotStatus.Compatible) return read;
            }
            return null;
        }, cancellationToken);

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        Run<object?>(token => { var path = SlotPath(id); CheckPath(path); token.ThrowIfCancellationRequested(); File.Delete(path); return null; }, cancellationToken);

    private ImmutableArray<GameSlotInfo> Scan(CancellationToken token) => Paths(token).Select(p => Read(p.Id, p.Path, token).Info)
        .OrderByDescending(s => s.Metadata?.SavedAtUtc ?? DateTimeOffset.MinValue)
        .ThenBy(s => s.Id, StringComparer.Ordinal).ToImmutableArray();

    private List<(string Id, string Path)> Paths(CancellationToken token)
    {
        CheckPath(directory);
        var result = new List<(string, string)>();
        // A broken/inaccessible quickslot is one row, just like a broken named slot.
        // File.Exists hides access errors and directories, so probe without swallowing them.
        try
        {
            CheckPath(quickPath);
            _ = File.GetAttributes(quickPath);
            result.Add((QuickSlotId, quickPath));
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { result.Add((QuickSlotId, quickPath)); }
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                var id = Path.GetFileNameWithoutExtension(path);
                if (!ValidId(id) || id == QuickSlotId) continue;
                result.Add((id, path));
                if (result.Count > MaxSlots) throw new InvalidOperationException("Каталог содержит больше 128 слотов.");
            }
        return result;
    }

    private GameSlotRead Read(string id, string path, CancellationToken token)
    {
        GameSlotMetadata? metadata = null;
        var metadataValid = false;
        var legacy = false;
        GameSlotRead Failure(GameSlotStatus status, string message) => new(new(id, status,
            metadataValid ? metadata : null, Clip(message, 1024), legacy), null);
        try
        {
            token.ThrowIfCancellationRequested(); CheckPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return Failure(GameSlotStatus.Corrupt, "Сохранение превышает 16 МиБ.");
            using var memory = new MemoryStream();
            var buffer = new byte[65536];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                if (memory.Length + count > MaxFileBytes) return Failure(GameSlotStatus.Corrupt, "Сохранение превышает 16 МиБ.");
                memory.Write(buffer, 0, count);
            }
            var text = Utf8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
            using var json = JsonDocument.Parse(text);
            var version = json.RootElement.GetProperty("Version").GetInt32();
            string saved;
            if (version == GameSave.CurrentVersion)
            { legacy = true; saved = text; }
            else if (version == CurrentVersion)
            {
                var document = JsonSerializer.Deserialize<Document>(text, Options)!;
                metadata = document.Metadata; saved = document.SaveJson;
            }
            else return Failure(GameSlotStatus.UnsupportedVersion, $"Версия слота {version} не поддерживается.");
            var state = GameSave.ReadState(saved);
            if (legacy)
            {
                var scene = project.Scenes.FirstOrDefault(s => s.Id == state.SceneId);
                metadata = new(state.ProjectId, state.Revision, File.GetLastWriteTimeUtc(path), Clip(scene?.Name ?? "Старое сохранение", 128),
                    state.SceneId, Clip(scene?.Name ?? "", 256), state.LineId, "", state.Time);
            }
            ValidateMetadata(metadata!, state);
            metadataValid = true;
            token.ThrowIfCancellationRequested();
            if (state.ProjectId != project.Id) return Failure(GameSlotStatus.ForeignProject, "Слот относится к другой новелле.");
            if (state.Revision != revision) return Failure(GameSlotStatus.IncompatibleRevision, "Слот относится к другой редакции проекта.");
            GameSave.Validate(state, project);
            token.ThrowIfCancellationRequested();
            return new(new(id, GameSlotStatus.Compatible, metadata, null, legacy), new(metadata!, saved));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return Failure(GameSlotStatus.Unavailable, error.Message); }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Failure(GameSlotStatus.Corrupt, error.Message); }
    }

    private static void ValidateMetadata(GameSlotMetadata metadata, GameState state)
    {
        if (metadata is null || !metadata.IsValid || metadata.ProjectId != state.ProjectId ||
            metadata.Revision != state.Revision || metadata.SceneId != state.SceneId ||
            metadata.LineId != state.LineId || metadata.GameTime != state.Time)
            throw new ArgumentException("Метаданные не соответствуют снимку сохранения.");
    }

    private string SlotPath(string id)
    {
        if (!ValidId(id)) throw new ArgumentException("ID слота: 1–64 символа a–z, 0–9, '_' или '-'; системные имена запрещены.");
        return id == QuickSlotId ? quickPath : Path.Combine(directory, id + ".json");
    }
    private static bool ValidId(string id) => IsValidSlotId(id);
    public static bool IsValidSlotId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 &&
        id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-') &&
        id is not ("con" or "prn" or "aux" or "nul") &&
        !(id.Length == 4 && (id.StartsWith("com", StringComparison.Ordinal) || id.StartsWith("lpt", StringComparison.Ordinal)) && id[3] is >= '0' and <= '9');

    private static void CheckPath(string path)
    {
        // Reject existing links, including links in the profile path. This is not an OS sandbox against concurrent path replacement.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Символические ссылки в пути слотов запрещены.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private async Task<T> Run<T>(Func<CancellationToken, T> action, CancellationToken token)
    {
        if (Interlocked.Increment(ref admitted) > 33)
        { Interlocked.Decrement(ref admitted); throw new InvalidOperationException("Очередь операций слотов заполнена."); }
        try
        {
            // Even directory enumeration/open/rename and JSON parsing must not run inline in Update.
            return await Task.Run(async () =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try { token.ThrowIfCancellationRequested(); return action(token); }
                finally { gate.Release(); }
            }, token).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref admitted); }
    }
    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
}
