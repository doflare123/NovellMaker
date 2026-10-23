using System.Collections.Immutable;

namespace NovellMaker.Core.Profiles;

public sealed record PlayerSettingsWrite(long Revision, bool Success, string Message);

/// <summary>Host-thread profile with bounded coalescing. Gameplay resets do not own this lifetime.</summary>
public sealed class PlayerSettingsSession(IPlayerProfileStore store, PlayerProfileRead initial)
{
    private Task? pending;
    private long revision;
    private long writing;
    private long persistedRevision = initial.Status is PlayerProfileStatus.Loaded or PlayerProfileStatus.Recovered or PlayerProfileStatus.Missing ? 0 : -1;
    public PlayerSettings Current { get; private set; } = initial.Settings;
    public ImmutableDictionary<string, string> Data { get; private set; } = initial.Data;
    public string Diagnostic { get; private set; } = initial.Diagnostic;
    public bool IsBusy => pending is not null;
    /// <summary>The current complete profile has no changes awaiting a successful write.</summary>
    public bool IsPersisted => revision == persistedRevision;
    public PlayerSettingsWrite? LastWrite { get; private set; }

    /// <summary>Startup fallback for an unsupported device mode; reading never repairs the disk profile.</summary>
    public void UseStartupFallback(PlayerSettings settings)
    {
        settings.Validate();
        if (revision != 0) throw new InvalidOperationException("Начальные настройки уже применены пользователем.");
        if (Current != settings) persistedRevision = -1;
        Current = settings;
    }

    /// <summary>Apply immediately. Repeating the same value explicitly retries a failed disk write.</summary>
    public long Set(PlayerSettings settings)
    {
        settings.Validate(); Current = settings;
        return Changed();
    }

    public string? GetPersistent(string key)
    {
        if (!AuthorKey.IsValid(key)) throw new ArgumentException("Некорректный ключ постоянных данных.");
        return Data.GetValueOrDefault(key);
    }

    /// <summary>Strings are author data; null removes a key. A repeated value can retry a failed write.</summary>
    public long SetPersistent(PersistentDataChange change)
    {
        change.Validate();
        if (GetPersistent(change.Key) == change.Value) return revision == persistedRevision || pending is not null ? revision : Changed();
        var candidate = change.Value is { } value ? Data.SetItem(change.Key, value) : Data.Remove(change.Key);
        new PlayerProfileData(Current, candidate).Validate();
        Data = candidate;
        return Changed();
    }

    private long Changed()
    {
        revision++;
        if (pending is null) StartWrite();
        return revision;
    }

    private void StartWrite()
    {
        writing = revision;
        // Invoke off-thread even for a custom store which may throw before returning a Task.
        var snapshot = new PlayerProfileData(Current, Data);
        pending = Task.Run(() => store.SaveAsync(snapshot));
    }

    public bool Poll()
    {
        if (pending is not { IsCompleted: true } task) return false;
        pending = null;
        try
        {
            task.GetAwaiter().GetResult(); Diagnostic = ""; persistedRevision = writing;
            LastWrite = new(writing, true, "Профиль сохранён.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Diagnostic = "Не удалось сохранить профиль. Изменения действуют до закрытия игры; повторите операцию для записи.";
            LastWrite = new(writing, false, Diagnostic);
        }
        if (revision != writing) StartWrite();
        return true;
    }

    /// <summary>Shutdown only, outside the game loop. Drains the newest snapshot without unbounded retries.</summary>
    public async Task FlushAsync()
    {
        while (pending is { } task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException) { }
            Poll();
        }
    }
}
