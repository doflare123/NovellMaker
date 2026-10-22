namespace NovellMaker.Core.Profiles;

public sealed record PlayerSettingsWrite(long Revision, bool Success, string Message);

/// <summary>Host-thread preferences with bounded coalescing. Gameplay resets do not own this lifetime.</summary>
public sealed class PlayerSettingsSession(IPlayerProfileStore store, PlayerProfileRead initial)
{
    private Task? pending;
    private long revision;
    private long writing;
    public PlayerSettings Current { get; private set; } = initial.Settings;
    public string Diagnostic { get; private set; } = initial.Diagnostic;
    public bool IsBusy => pending is not null;
    public PlayerSettingsWrite? LastWrite { get; private set; }

    /// <summary>Startup fallback for an unsupported device mode; reading never repairs the disk profile.</summary>
    public void UseStartupFallback(PlayerSettings settings)
    {
        settings.Validate();
        if (revision != 0) throw new InvalidOperationException("Начальные настройки уже применены пользователем.");
        Current = settings;
    }

    /// <summary>Apply immediately. Repeating the same value explicitly retries a failed disk write.</summary>
    public long Set(PlayerSettings settings)
    {
        settings.Validate(); Current = settings; revision++;
        if (pending is null) StartWrite();
        return revision;
    }

    private void StartWrite()
    {
        writing = revision;
        // Invoke off-thread even for a custom store which may throw before returning a Task.
        var snapshot = Current;
        pending = Task.Run(() => store.SaveAsync(snapshot));
    }

    public bool Poll()
    {
        if (pending is not { IsCompleted: true } task) return false;
        pending = null;
        try
        {
            task.GetAwaiter().GetResult(); Diagnostic = "";
            LastWrite = new(writing, true, "Настройки сохранены.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            Diagnostic = "Не удалось сохранить профиль. Настройки действуют до закрытия игры; повторите изменение для записи.";
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
