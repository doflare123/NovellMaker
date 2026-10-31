using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Text;

namespace NovellMaker.Core.Execution;

public enum ReadingMode { Manual, Auto, SkipRead }

/// <summary>Host-driven presentation. Never seeks or bypasses scenario barriers; at most one advance per frame.</summary>
public sealed class ReadingPlayback(PlayerSettingsSession profile)
{
    private ScenarioPlayer? execution;
    private long restoreGeneration;
    private (Guid? Line, Guid Visit, double Start) position;
    private string text = "";
    private int count;
    private double visible;
    private double readySeconds;
    private string? readKey;
    private bool wasRead;
    private int presentedElements = -1;
    private string visibleText = "";
    public ReadingMode Mode { get; private set; }
    public bool FullyRevealed => visible >= count;
    public string VisibleText
    {
        get
        {
            if (FullyRevealed) return text;
            var elements = (int)visible;
            if (elements != presentedElements) { visibleText = DialogueText.Prefix(text, elements); presentedElements = elements; }
            return visibleText;
        }
    }
    public void Stop() { Mode = ReadingMode.Manual; readySeconds = 0; }

    public void Synchronize(ScenePlayer player)
    {
        var state = player.Execution.State;
        var changedPlayer = !ReferenceEquals(execution, player.Execution) || restoreGeneration != player.Execution.RestoreGeneration;
        if (changedPlayer) { Stop(); execution = player.Execution; restoreGeneration = execution.RestoreGeneration; }
        var next = (state.LineId, state.History.LastOrDefault()?.VisitId ?? Guid.Empty, state.TextStartedAt);
        if (changedPlayer || next != position)
        {
            position = next; text = player.Frame.Text;
            presentedElements = -1;
            count = StringInfo.ParseCombiningCharacters(string.Concat(DialogueText.Parse(text).Select(r => r.Text))).Length;
            visible = changedPlayer && restoreGeneration > 0 || profile.Current.TextSpeed == 0 ? count : 0;
            readySeconds = 0;
            readKey = state.LineId is { } line ? ReadKey(state.SceneId, line, execution!.Language, text) : null;
            wasRead = readKey is not null && profile.HasRead(readKey);
            if (Mode == ReadingMode.SkipRead && !wasRead) Stop();
        }
        if (state.Choice is not null || state.Minigame is not null || state.Wait?.Mode == "interaction" || state.Error is not null || state.Completed) Stop();
    }

    public static string ReadKey(Guid scene, Guid line, string language, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scene:D}/{line:D}/{language.Length}:{language}/{text}")));

    public bool Toggle(ScenePlayer player, ReadingMode mode)
    {
        Synchronize(player);
        if (Mode == mode) { Stop(); return true; }
        var state = player.Execution.State;
        if (mode == ReadingMode.SkipRead) wasRead = readKey is not null && profile.HasRead(readKey);
        if (mode == ReadingMode.Manual) { Stop(); return true; }
        if (state.LineId is null || state.Choice is not null || state.Minigame is not null || state.Wait?.Mode == "interaction" ||
            state.Error is not null || state.Completed || mode == ReadingMode.SkipRead && !wasRead) return false;
        Mode = mode; readySeconds = 0; return true;
    }

    /// <summary>The first input completes the current text; the next continues. Manual input cancels automation.</summary>
    public bool Advance(ScenePlayer player)
    {
        Synchronize(player); Stop();
        if (player.Execution.IsPaused || player.Execution.HasPendingLua || player.Execution.State.Minigame is not null) return false;
        if (!FullyRevealed && player.Frame.LineId is not null) { visible = count; MarkRead(); return true; }
        var result = player.Next(); Synchronize(player); return result;
    }

    public bool Update(ScenePlayer player, TimeSpan elapsed, bool active, bool voicePlaying)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        Synchronize(player);
        if (!active || player.Execution.IsPaused || player.Execution.HasPendingLua) return false;
        var seconds = elapsed.TotalSeconds;
        var revealSeconds = FullyRevealed || profile.Current.TextSpeed == 0 || Mode == ReadingMode.SkipRead ? 0 : (count - visible) / profile.Current.TextSpeed;
        if (profile.Current.TextSpeed == 0 || Mode == ReadingMode.SkipRead) visible = count;
        else visible = Math.Min(count, visible + seconds * profile.Current.TextSpeed);
        if (!FullyRevealed) return false;
        MarkRead();
        if (player.Execution.State.Wait?.Mode != "input") { readySeconds = 0; return false; }
        if (!player.Frame.CanNext) { Stop(); return false; }
        readySeconds += Math.Max(0, seconds - revealSeconds);
        if (Mode == ReadingMode.Manual || Mode == ReadingMode.Auto && (readySeconds < profile.Current.AutoDelay || profile.Current.AutoWaitVoice && voicePlaying) ||
            Mode == ReadingMode.SkipRead && (!wasRead || readySeconds < .08)) return false;
        var changed = player.Next(); readySeconds = 0; Synchronize(player); return changed;
    }

    private void MarkRead() { if (readKey is not null) profile.MarkRead(readKey); }
}
