using System.Collections.Immutable;
using NovellMaker.Core.Audio;

namespace NovellMaker.Core.Screens.Lua;

public sealed partial class ScreenLuaSession
{
    private sealed class AudioTicket(int depth)
    {
        internal readonly int Depth = depth;
        internal ScriptAudioResult? Result;
        internal bool Queued;
    }
    private AudioService? audio;

    /// <summary>The host retains service ownership. A session releases only its own sources.</summary>
    public void ConnectAudio(AudioService service)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ReferenceEquals(audio, service)) return;
        if (entries.Values.Any(e => e.Audio.Count != 0)) throw new InvalidOperationException("Нельзя заменить звук при живых источниках Lua.");
        if (audio is not null) audio.PlaybackEnded -= AudioEnded;
        audio = service ?? throw new ArgumentNullException(nameof(service));
        audio.PlaybackEnded += AudioEnded;
    }
    public int ActiveAudio => entries.Values.Sum(e => e.Audio.Count(p => p.Value.Result is null));
    public bool HasAudio(ScreenInstance instance) => IsAvailable(instance) &&
        (entries[instance.Id].Audio.Count != 0 || queue.Any(w => ReferenceEquals(w.Entry.Instance, instance) && w.Event?.Audio is not null) ||
         pending is { } p && ReferenceEquals(p.Work.Entry.Instance, instance) && p.Work.Event?.Audio is not null ||
         held.TryGetValue(instance.Id, out var h) && h.Work.Event?.Audio is not null);

    /// <summary>Host frame: reconcile ownership before touching a source, then exchange reserved results for events.</summary>
    public void PollAudio(Func<ScreenInstance, bool> active, ImmutableDictionary<string, string>? snapshot = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Reconcile();
        foreach (var entry in entries.Values)
            if (IsAvailable(entry.Instance)) audio?.PauseOwner(entry.AudioOwner, entry.SuspendedAt is not null || !active(entry.Instance) || HoldsAudio(entry));
        audio?.Update();
        foreach (var entry in entries.Values)
        {
            if (!IsAvailable(entry.Instance)) continue;
            foreach (var pair in entry.Audio.Where(p => p.Value.Result is not null && !p.Value.Queued).ToArray())
            {
                pair.Value.Queued = true;
                queue.AddLast(new Work(entry, "event", new("audio_result") { Audio = pair.Value.Result }, snapshot ?? entry.Snapshot,
                    clock.GetTimestamp(), pair.Value.Depth));
            }
        }
    }
    // An update computes its batch asynchronously while host-owned streaming continues.
    // Audio is not part of its Lua snapshot. Input/results/load still retain the existing hold policy.
    private bool HoldsAudio(Entry entry)
    {
        static bool Barrier(Work work) => work.Kind != "event" || work.Event?.Kind != "update";
        return !entry.Loaded || pending is { } p && ReferenceEquals(p.Work.Entry, entry) && Barrier(p.Work) ||
            held.TryGetValue(entry.Instance.Id, out var h) && Barrier(h.Work) ||
            queue.Any(w => ReferenceEquals(w.Entry, entry) && Barrier(w));
    }
    private void AudioEnded(AudioOwner owner, ScriptAudioResult result)
    {
        if (entries.TryGetValue(owner.InstanceId, out var entry) && entry.AudioOwner == owner &&
            entry.Audio.TryGetValue(result.Id, out var ticket)) ticket.Result ??= result;
    }
    private int ValidateAudio(Entry entry, ImmutableArray<ScreenChange> changes, int depth)
    {
        var ids = entry.Audio.Keys.ToHashSet(); var added = 0;
        foreach (var change in changes)
        {
            if (change.Audio is not { } command) continue;
            if (audio is null) throw new InvalidOperationException("AudioService для Lua недоступен.");
            command.Validate();
            if (command.Kind == "play")
            {
                if (!entry.AudioReferences.ContainsValue(command.AssetId!.Value) || !ids.Add(command.Id))
                    throw new InvalidOperationException("Чужой ресурс или повторный ID звука Lua.");
                if (depth >= NovellMaker.Core.Triggers.AuthorTrigger.MaximumChainDepth)
                    throw new InvalidOperationException("Превышена глубина цепочки результатов звука.");
                added++;
            }
            else if (!ids.Contains(command.Id)) throw new InvalidOperationException("Чужой или устаревший источник звука Lua.");
        }
        return added;
    }
    private void CommitAudio(Entry entry, ImmutableArray<ScreenChange> changes, int depth)
    {
        foreach (var change in changes)
        {
            if (change.Audio is not { } command) continue;
            if (command.Kind == "play") entry.Audio.Add(command.Id, new(depth + 1));
            audio!.Execute(entry.AudioOwner, command);
        }
    }
    private void ClearAudio(Entry entry) { audio?.StopOwner(entry.AudioOwner); entry.Audio.Clear(); }
}
