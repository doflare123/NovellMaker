using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NovellMaker.Core.Profiles;

public enum PlayerProfileStatus { Missing, Loaded, Recovered, Corrupt, UnsupportedVersion, ForeignProject, Unavailable }
public sealed record PlayerProfileRead(PlayerSettings Settings, PlayerProfileStatus Status, string Diagnostic)
{
    public ImmutableDictionary<string, string> Data { get; init; } = ImmutableDictionary<string, string>.Empty;
}
public interface IPlayerProfileStore
{
    Task<PlayerProfileRead> ReadAsync(PlayerSettings defaults, CancellationToken token = default);
    Task SaveAsync(PlayerProfileData profile, CancellationToken token = default);
}

/// <summary>Small versioned preferences file; no project data, gameplay state or script execution.</summary>
public sealed class PlayerProfileStore : IPlayerProfileStore
{
    public const int CurrentVersion = 3;
    public const int MaxFileBytes = 128 * 1024;
    private const int LegacyMaxFileBytes = 16384;
    private sealed record LegacyDocument(int Version, Guid ProjectId, PlayerSettings Settings);
    private sealed record LegacyV2Document(int Version, Guid ProjectId, PlayerSettings Settings, ImmutableArray<string> Achievements);
    private sealed record Document(int Version, Guid ProjectId, PlayerSettings Settings, ImmutableDictionary<string, string> Data);
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
                    "Профиль восстановлен из резервной копии. Последние изменения могли не сохраниться.") { Data = backup.Data };
            if (result.Status == PlayerProfileStatus.Missing && backup.Status != PlayerProfileStatus.Missing)
                return backup with { Diagnostic = "Основной профиль отсутствует. Резервная копия не прочитана; используются настройки проекта." };
        }
        return result;
    }, token);

    public Task SaveAsync(PlayerProfileData profile, CancellationToken token = default)
    {
        profile.Validate();
        return Save(profile.Settings, profile.Data, token);
    }

    /// <summary>Settings-only callers preserve the latest validated author data.</summary>
    public Task SaveAsync(PlayerSettings settings, CancellationToken token = default) => Save(settings, null, token);

    private Task Save(PlayerSettings settings, ImmutableDictionary<string, string>? data, CancellationToken token) => Run<object?>(() =>
    {
        settings.Validate(); token.ThrowIfCancellationRequested(); CheckPath(path);
        var existing = Read(path, settings);
        EnsureWritable(existing);
        // Both files are protected, even when the primary is missing/corrupt or a valid
        // primary would otherwise rotate over an unreadable/foreign/future backup.
        var backup = Read(path + ".bak", settings);
        EnsureWritable(backup);
        var values = data ?? (existing.Status == PlayerProfileStatus.Loaded ? existing.Data : backup.Data);
        new PlayerProfileData(settings, values).Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); CheckPath(path);
        var bytes = Serialize(settings, values);
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("Профиль превышает допустимый размер.");
        // Back up only a fully validated document; never replace a good backup with a corrupt primary.
        if (existing.Status == PlayerProfileStatus.Loaded)
            AtomicWrite(path + ".bak", Serialize(existing.Settings, existing.Data), token);
        AtomicWrite(path, bytes, token);
        return null;
    }, token);

    private byte[] Serialize(PlayerSettings settings, ImmutableDictionary<string, string> data) => JsonSerializer.SerializeToUtf8Bytes(
        new { Version = CurrentVersion, ProjectId = projectId, Settings = settings, Data = data.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal) }, Json);

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
            // A supported duplicate must not mask a readable future version either.
            if (json.RootElement.EnumerateObject().Any(p => p.NameEquals("Version") &&
                p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var declared) && declared is not (1 or 2 or CurrentVersion)))
                return new(defaults, PlayerProfileStatus.UnsupportedVersion, "Версия профиля не поддерживается; используются настройки проекта.");
            var version = json.RootElement.GetProperty("Version").GetInt32();
            if (version < CurrentVersion && count > LegacyMaxFileBytes)
                return new(defaults, PlayerProfileStatus.Corrupt, "Профиль прежней версии превышает допустимый размер.");
            if (version == 1)
            {
                var legacy = JsonSerializer.Deserialize<LegacyDocument>(bytes.AsSpan(0, count), Json)!;
                legacy.Settings.Validate();
                return new(legacy.Settings, PlayerProfileStatus.Loaded, "Профиль v1 прочитан; v3 будет записан при следующем изменении.");
            }
            if (version == 2)
            {
                var legacy = JsonSerializer.Deserialize<LegacyV2Document>(bytes.AsSpan(0, count), Json)!;
                if (legacy.Achievements.IsDefault || legacy.Achievements.Length > 128 ||
                    legacy.Achievements.Any(id => !AuthorKey.IsValid(id)) ||
                    legacy.Achievements.Distinct(StringComparer.Ordinal).Count() != legacy.Achievements.Length)
                    throw new ArgumentException("Некорректный профиль v2.");
                var data = legacy.Achievements.ToImmutableDictionary(id => id, _ => "true", StringComparer.Ordinal);
                new PlayerProfileData(legacy.Settings, data).Validate();
                return new(legacy.Settings, PlayerProfileStatus.Loaded, "Профиль v2 прочитан; v3 будет записан при следующем изменении.") { Data = data };
            }
            var document = JsonSerializer.Deserialize<Document>(bytes.AsSpan(0, count), Json)!;
            new PlayerProfileData(document.Settings, document.Data).Validate();
            return new(document.Settings, PlayerProfileStatus.Loaded, "") { Data = document.Data };
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
