using System.Collections.Immutable;
using NovellMaker.Core.Scripting;

namespace NovellMaker.Runtime;

/// <summary>One writer and one latest snapshot. Disk work never runs on Update/Draw.</summary>
internal sealed class LuaJournalFile(string path)
{
    private readonly object gate = new();
    private (ImmutableArray<LuaDiagnosticEntry> Entries, long Evicted)? latest;
    private Task writer = Task.CompletedTask;
    private long sequence = -1;
    private volatile string? error;
    internal string? Error => error;

    internal void Publish(LuaDiagnosticJournal journal)
    {
        if (sequence == journal.Sequence) return;
        sequence = journal.Sequence;
        lock (gate)
        {
            latest = (journal.Entries, journal.Evicted);
            if (writer.IsCompleted) writer = Task.Run(WriteAsync);
        }
    }

    private async Task WriteAsync()
    {
        while (true)
        {
            (ImmutableArray<LuaDiagnosticEntry> Entries, long Evicted) snapshot;
            lock (gate)
            {
                if (latest is not { } value) { writer = Task.CompletedTask; return; }
                snapshot = value; latest = null;
            }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var text = $"NovellMaker Lua journal; last {LuaDiagnosticJournal.MaximumEntries}; evicted={snapshot.Evicted}\n" +
                    string.Join('\n', snapshot.Entries.Select(e => e.Format())) + "\n";
                await File.WriteAllTextAsync(temporary, text, new System.Text.UTF8Encoding(false)).ConfigureAwait(false);
                File.Move(temporary, path, true);
                error = null;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { error = "Не удалось записать журнал Lua в профиль пользователя."; }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    internal async Task FlushAsync()
    {
        Task current; lock (gate) current = writer;
        try { await current.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { error = "Не завершена запись журнала Lua за 3 секунды."; }
    }
}
