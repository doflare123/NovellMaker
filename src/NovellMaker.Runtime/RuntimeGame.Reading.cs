using System.Collections.Immutable;
using Microsoft.Xna.Framework;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Profiles;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly ReadingPlayback reading;
    private ScreenInstance? readingPanel;
    private bool historyPanel;
    private int historyIndex;
    private int historyPage;
    private readonly Dictionary<Guid, Action> readingButtons = [];

    private void UpdateReading(TimeSpan elapsed, bool active)
    {
        SynchronizeReadingPanel();
        audio?.PauseReplayVoice(!active || readingPanel is null || !ReferenceEquals(screens.Stack.Instances.LastOrDefault(), readingPanel));
        if (screens.HasOverlays || session.Phase != GameSessionPhase.Playing) reading.Stop();
        reading.Update(player, elapsed, active && session.Phase == GameSessionPhase.Playing && !screens.HasOverlays, audio?.IsVoicePlaying == true);
    }

    private void SynchronizeReadingPanel()
    {
        if (readingPanel is { } panel && !screens.Stack.Instances.Contains(panel))
        { readingPanel = null; readingButtons.Clear(); audio?.StopReplayVoice(); }
    }

    private bool ReadingAction(ScreenButtonAction action)
    {
        switch (action)
        {
            case ScreenButtonAction.History:
                if (session.Phase != GameSessionPhase.Playing) return false;
                if (readingPanel is not null && historyPanel) { CloseReadingPanel(); return true; }
                historyPanel = true; historyIndex = Math.Max(0, player.Execution.State.History.Length - 1); historyPage = 0;
                ShowReadingPanel(); return true;
            case ScreenButtonAction.ReadingSettings:
                if (readingPanel is not null && !historyPanel) { CloseReadingPanel(); return true; }
                historyPanel = false; ShowReadingPanel(); return true;
            case ScreenButtonAction.AutoRead:
            case ScreenButtonAction.SkipRead:
                return session.Phase == GameSessionPhase.Playing && !screens.HasOverlays && reading.Toggle(player,
                    action == ScreenButtonAction.AutoRead ? ReadingMode.Auto : ReadingMode.SkipRead);
            default: return false;
        }
    }

    private void CloseReadingPanel()
    {
        if (readingPanel is { } panel && screens.Stack.Instances.Contains(panel)) screens.Stack.Close(panel);
        readingPanel = null; readingButtons.Clear(); audio?.StopReplayVoice();
    }

    private bool ActivateReading(ScreenActivation activation)
    {
        if (readingPanel?.Id != activation.InstanceId || !readingButtons.TryGetValue(activation.NodeId, out var action)) return false;
        action(); return true;
    }

    private void ShowReadingPanel()
    {
        reading.Stop(); audio?.StopReplayVoice(); readingButtons.Clear();
        var root = Guid.NewGuid();
        var nodes = ImmutableArray.CreateBuilder<ScreenNode>();
        nodes.Add(new(root, null, ScreenNodeKind.Panel, ScreenDefinition.CanvasViewport));
        void Text(string value, double y, double width = 1120) => nodes.Add(new(Guid.NewGuid(), root, ScreenNodeKind.Text, new(80, y, width, 28), value));
        void Button(string value, double x, double y, double width, Action action, bool enabled = true)
        {
            var id = Guid.NewGuid(); nodes.Add(new(id, root, ScreenNodeKind.Button, new(x, y, width, 44), value) { Enabled = enabled });
            readingButtons.Add(id, action);
        }
        Text(historyPanel ? "История реплик" : "Настройки чтения", 32);
        Button("Закрыть", 1040, 642, 160, CloseReadingPanel);
        if (historyPanel)
        {
            var history = player.Execution.State.History;
            if (history.IsEmpty) Text("В этом сохранении пока нет истории реплик.", 110);
            else
            {
                historyIndex = Math.Clamp(historyIndex, 0, history.Length - 1);
                var entry = history[historyIndex];
                var scene = player.Execution.Project.Scenes.First(s => s.Id == entry.SceneId);
                var line = scene.Lines.First(l => l.Id == entry.LineId);
                var text = line.Texts.GetValueOrDefault(entry.Language) ?? line.Texts.GetValueOrDefault(player.Execution.Project.DefaultLanguage, "");
                var speaker = player.Execution.Project.Characters.FirstOrDefault(c => c.Id == line.SpeakerId)?.Name ?? "";
                Text($"{historyIndex + 1} / {history.Length} · {entry.Language} · {speaker}", 80);
                var lines = DialogueTextureRenderer.HistoryLines(text);
                const int perPage = 16;
                var pages = Math.Max(1, (lines.Count + perPage - 1) / perPage);
                historyPage = Math.Clamp(historyPage, 0, pages - 1);
                for (var i = 0; i < perPage && historyPage * perPage + i < lines.Count; i++) Text(lines[historyPage * perPage + i], 126 + i * 28);
                Button("Ранее", 80, 590, 160, () => { historyIndex--; historyPage = 0; ShowReadingPanel(); }, historyIndex > 0);
                Button("Позже", 250, 590, 160, () => { historyIndex++; historyPage = 0; ShowReadingPanel(); }, historyIndex < history.Length - 1);
                Button("Страница −", 440, 590, 180, () => { historyPage--; ShowReadingPanel(); }, historyPage > 0);
                Button($"{historyPage + 1}/{pages} · +", 630, 590, 180, () => { historyPage++; ShowReadingPanel(); }, historyPage < pages - 1);
                Button("Повторить голос", 850, 590, 350, () => audio?.ReplayVoice(entry.LineId, entry.Language), audio?.CanReplayVoice(entry.LineId, entry.Language) == true);
            }
        }
        else
        {
            var value = settings.Current;
            Text(value.TextSpeed == 0 ? "Появление текста: мгновенно" : $"Скорость текста: {value.TextSpeed:0.#} знаков/с", 130);
            Button("Медленнее", 80, 180, 220, () => SetReading(p => p with { TextSpeed = Math.Max(0, p.TextSpeed - 10) }));
            Button("Быстрее", 320, 180, 220, () => SetReading(p => p with { TextSpeed = Math.Min(200, p.TextSpeed + 10) }));
            Button("Мгновенно", 560, 180, 220, () => SetReading(p => p with { TextSpeed = 0 }));
            Text($"Авточтение: задержка после текста {value.AutoDelay:0.#} с", 285);
            Button("−0,5 с", 80, 335, 220, () => SetReading(p => p with { AutoDelay = Math.Max(0, p.AutoDelay - .5) }));
            Button("+0,5 с", 320, 335, 220, () => SetReading(p => p with { AutoDelay = Math.Min(30, p.AutoDelay + .5) }));
            Button(value.AutoWaitVoice ? "Ждать окончания голоса: да" : "Ждать окончания голоса: нет", 80, 430, 700,
                () => SetReading(p => p with { AutoWaitVoice = !p.AutoWaitVoice }));
            Text("Пропуск останавливается на непрочитанной реплике, выборе и мини-игре.", 530);
            Text(settings.Diagnostic, 580);
        }
        var definition = new ScreenDefinition(Guid.NewGuid(), nodes.ToImmutable());
        readingPanel = readingPanel is { } current && screens.Stack.Instances.Contains(current)
            ? screens.Stack.Replace(current, definition, new()) : screens.Stack.Open(Guid.NewGuid(), definition, new());
    }

    private void SetReading(Func<PlayerSettings, PlayerSettings> change)
    {
        var value = change(settings.Current);
        ApplyPlayerSettings(value); settings.Set(value); ShowReadingPanel();
    }
}
