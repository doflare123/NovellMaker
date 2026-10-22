using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Profiles;

public enum PlayerProfileStatus { Missing, Loaded, Recovered, Corrupt, UnsupportedVersion, ForeignProject, Unavailable }
public sealed record PlayerProfileRead(PlayerSettings Settings, PlayerProfileStatus Status, string Diagnostic);
public interface IPlayerProfileStore
{
    Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default);
    Task SaveAsync(PlayerSettings settings, CancellationToken token = default);
}

/// <summary>Small versioned preferences file; no project data, gameplay state or script execution.</summary>
public sealed class PlayerProfileStore : IPlayerProfileStore
{
    public const int CurrentVersion = 1;
    public const int MaxFileBytes = 16384;
    private sealed record Document(int Version, Guid ProjectId, PlayerSettings Settings);
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };
    private readonly Guid projectId;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int admitted;

    public PlayerProfileStore(Guid projectId, string path)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Пустой ID профиля.");
        this.projectId = projectId; this.path = Path.GetFullPath(path);
    }

    public Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default) => Run(() =>
    {
        defaults.Validate(); token.ThrowIfCancellationRequested();
        var result = Read(path, defaults);
        if (result.Status is PlayerProfileStatus.Corrupt or PlayerProfileStatus.Missing)
        {
            var backup = Read(path + ".bak", defaults);
            if (backup.Status == PlayerProfileStatus.Loaded)
                return new PlayerProfileRead(backup.Settings, PlayerProfileStatus.Recovered,
                    "Профиль восстановлен из резервной копии. Последние изменения могли не сохраниться.");
            if (result.Status == PlayerProfileStatus.Missing && backup.Status != PlayerProfileStatus.Missing)
                return backup with { Diagnostic = "Основной профиль отсутствует. Резервная копия не прочитана; используются настройки проекта." };
        }
        return result;
    }, token);

    public Task SaveAsync(PlayerSettings settings, CancellationToken token = default) => Run<object?>(() =>
    {
        settings.Validate(); token.ThrowIfCancellationRequested(); CheckPath(path);
        var existing = Read(path, settings);
        EnsureWritable(existing);
        // Both files are protected, even when the primary is missing/corrupt or a valid
        // primary would otherwise rotate over an unreadable/foreign/future backup.
        EnsureWritable(Read(path + ".bak", settings));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); CheckPath(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(CurrentVersion, projectId, settings), Json);
        // Back up only a fully validated document; never replace a good backup with a corrupt primary.
        if (existing.Status == PlayerProfileStatus.Loaded)
            AtomicWrite(path + ".bak", JsonSerializer.SerializeToUtf8Bytes(new Document(CurrentVersion, projectId, existing.Settings), Json), token);
        AtomicWrite(path, bytes, token);
        return null;
    }, token);

    private static void EnsureWritable(PlayerProfileRead existing)
    {
        if (existing.Status is PlayerProfileStatus.UnsupportedVersion or PlayerProfileStatus.ForeignProject)
            throw new IOException("Профиль другой версии или новеллы: запись запрещена.");
        if (existing.Status == PlayerProfileStatus.Unavailable) throw new IOException("Профиль недоступен для записи.");
    }

    private PlayerProfileRead Read(string file, PlayerSettings defaults)
    {
        try
        {
            CheckPath(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaxFileBytes) return new(defaults, PlayerProfileStatus.Corrupt, "Профиль превышает допустимый размер.");
            // A bounded read also rejects a file which grows after opening.
            var bytes = new byte[MaxFileBytes + 1]; var count = 0;
            while (count < bytes.Length)
            { var read = stream.Read(bytes, count, bytes.Length - count); if (read == 0) break; count += read; }
            if (count > MaxFileBytes) return new(defaults, PlayerProfileStatus.Corrupt, "Профиль превышает допустимый размер.");
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            // Ownership is independent of the settings/schema's validity. Inspect every
            // readable ID so a duplicate own ID cannot mask a foreign one before strict parsing.
            foreach (var property in json.RootElement.EnumerateObject())
                if (property.NameEquals("ProjectId") && property.Value.ValueKind == JsonValueKind.String &&
                    property.Value.TryGetGuid(out var owner) && owner != projectId)
                    return new(defaults, PlayerProfileStatus.ForeignProject, "Профиль относится к другой новелле; используются настройки проекта.");
            if (json.RootElement.GetProperty("Version").GetInt32() != CurrentVersion)
                return new(defaults, PlayerProfileStatus.UnsupportedVersion, "Версия профиля не поддерживается; используются настройки проекта.");
            var document = JsonSerializer.Deserialize<Document>(bytes.AsSpan(0, count), Json)!;
            document.Settings.Validate();
            return new(document.Settings, PlayerProfileStatus.Loaded, "");
        }
        catch (FileNotFoundException) { return new(defaults, PlayerProfileStatus.Missing, ""); }
        catch (DirectoryNotFoundException) { return new(defaults, PlayerProfileStatus.Missing, ""); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(defaults, PlayerProfileStatus.Unavailable, "Не удалось прочитать профиль; используются настройки проекта."); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return new(defaults, PlayerProfileStatus.Corrupt, "Профиль повреждён; используются настройки проекта."); }
    }

    private static void AtomicWrite(string file, byte[] bytes, CancellationToken token)
    {
        CheckPath(file);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); CheckPath(file);
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void CheckPath(string file)
    {
        for (string? current = file; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Ссылки в пути профиля запрещены.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private async Task<T> Run<T>(Func<T> action, CancellationToken token)
    {
        if (Interlocked.Increment(ref admitted) > 33)
        { Interlocked.Decrement(ref admitted); throw new InvalidOperationException("Очередь профиля заполнена."); }
        try
        {
            return await Task.Run(async () =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try { token.ThrowIfCancellationRequested(); return action(); }
                finally { gate.Release(); }
            }, token).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref admitted); }
    }
}
