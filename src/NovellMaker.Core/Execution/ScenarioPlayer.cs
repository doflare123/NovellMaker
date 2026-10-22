using System.Collections.Immutable;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;

namespace NovellMaker.Core.Execution;

/// <summary>Shared action interpreter, driven exclusively by host input and elapsed game time.</summary>
public sealed partial class ScenarioPlayer : IDisposable, IAsyncDisposable
{
    private readonly NovelProject project;
    private readonly string language;
    private readonly Dictionary<Guid, ImmutableArray<ScenarioAction>> programs;
    private int variableCharacters;
    public GameState State { get; private set; }
    public NovelProject Project => project;
    private readonly Queue<AudioEvent> audioEvents = new();
    private void Audio(AudioEventKind kind, Guid? line = null) => audioEvents.Enqueue(new(kind, State.SceneId, line));
    public AudioEvent[] DrainAudioEvents() { var result = audioEvents.ToArray(); audioEvents.Clear(); return result; }


    public void Cancel()
    {
        CancelPendingLua();
        State = State with { ActiveActions = [], QueuedActions = [], Wait = null, Choice = null };
        Fail("Исполнение отменено.");
    }

    public ScenarioPlayer(NovelProject project, Guid sceneId, string language)
        : this(project, sceneId, language, LuaActionScript.RequestCommandsAsync) { }

    internal ScenarioPlayer(NovelProject project, Guid sceneId, string language,
        Func<ScriptAction, GameState, NovelProject, CancellationToken, Task<ImmutableArray<ScenarioAction>>> runLua, bool startImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        this.project = project;
        this.language = language;
        this.runLua = runLua;
        programs = project.Scenes.ToDictionary(s => s.Id, ScenarioProgram.Compile);
        if (!programs.TryGetValue(sceneId, out var actions)) throw new ArgumentException($"Сцена {sceneId} отсутствует.", nameof(sceneId));
        State = new GameState { ProjectId = project.Id, Revision = ScenarioProgram.Fingerprint(project), SceneId = sceneId, NextActionId = actions.FirstOrDefault()?.Id };
        if (startImmediately) { Audio(AudioEventKind.Scene); Pump(); }
        else { State = State with { NextActionId = null }; IsPaused = true; }
    }

    public bool Advance()
    {
        if (disposed || IsPaused || HasPendingLua || State.Wait?.Mode != "input" || State.Error is not null) return false;
        Audio(AudioEventKind.Continue);
        State = State with { Wait = null };
        Pump();
        return true;
    }

    /// <summary>Explicit host navigation from a scene button. Starts the target once, preserving variables and time.</summary>
    public bool EnterScene(Guid sceneId)
    {
        if (disposed || !programs.TryGetValue(sceneId, out var actions)) return false;
        CancelPendingLua();
        ResetPass();
        audioEvents.Clear();
        State = new GameState
        {
            ProjectId = project.Id, Revision = State.Revision, SceneId = sceneId,
            NextActionId = actions.FirstOrDefault()?.Id, Variables = State.Variables, Time = State.Time
        };
        Audio(AudioEventKind.Scene);
        Pump();
        return true;
    }

    public bool Choose(Guid optionId)
    {
        if (disposed || IsPaused || HasPendingLua || State.Error is not null || State.Choice?.Options.FirstOrDefault(o => o.Id == optionId) is not { } option) return false;
        State = State with { Choice = null };
        Jump(option.TargetSceneId, option.TargetActionId);
        Pump();
        return true;
    }

    public bool Click(Guid objectId)
    {
        if (disposed || IsPaused || HasPendingLua || State.Error is not null || State.Wait is not { Mode: "interaction" } wait || wait.Token is not null && wait.Token != objectId ||
            !State.Hotspots.Any(h => h.ObjectId == objectId && h.Enabled)) return false;
        try { SetVariable("input.object", objectId.ToString()); }
        catch (InvalidOperationException error) { Fail(error.Message); return false; }
        State = State with { Wait = null, Hotspots = [] };
        Pump();
        return true;
    }

    public void Tick(TimeSpan elapsed)
    {
        var remaining = elapsed.TotalSeconds;
        if (!double.IsFinite(remaining) || remaining < 0) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (disposed || IsPaused || State.Error is not null) return;
        // Pending wall time is never added to scenario time, including the completion frame.
        if (HasPendingLua) { PollLua(); return; }
        // Split at wait boundaries, so a long frame agrees with several short frames.
        for (var steps = 0; remaining > 0 && steps < 2048; steps++)
        {
            var step = remaining;
            if (State.Wait is { Mode: "timer" } timer) step = Math.Min(step, timer.Remaining);
            if (State.Wait?.Mode == "actions" && !State.ActiveActions.IsEmpty)
                step = Math.Min(step, State.ActiveActions.Max(a => a.Duration - a.Elapsed));
            var placements = State.Placements;
            var active = ImmutableArray.CreateBuilder<ActiveMove>();
            foreach (var move in State.ActiveActions)
            {
                var passed = Math.Min(move.Duration, move.Elapsed + step);
                var t = move.Duration == 0 ? 1 : passed / move.Duration;
                placements = placements.SetItem(move.ObjectId, new CharacterPlacement(
                    Lerp(move.From.X, move.Target.X, t), Lerp(move.From.Y, move.Target.Y, t),
                    Lerp(move.From.Scale, move.Target.Scale, t), move.Target.Layer));
                if (passed < move.Duration) active.Add(move with { Elapsed = passed });
            }
            var wait = State.Wait;
            if (wait?.Mode == "timer") wait = wait with { Remaining = Math.Max(0, wait.Remaining - step) };
            if (wait is { Mode: "timer", Remaining: <= 0 } || wait?.Mode == "actions" && active.Count == 0) wait = null;
            State = State with { Time = State.Time + step, Placements = placements, ActiveActions = active.ToImmutable(), Wait = wait };
            remaining -= step;
            Pump();
            if (State.Error is not null || HasPendingLua) return;
            if (step == 0 && State.Wait is not null) break;
        }
        if (remaining > 0) Fail("Превышен лимит переходов ожиданий за кадр.");
    }

    public SceneFrame Frame
    {
        get
        {
            var scene = project.Scenes.First(s => s.Id == State.SceneId);
            var line = scene.Lines.FirstOrDefault(l => l.Id == State.LineId);
            var diagnostics = State.Diagnostics.ToBuilder();
            DialogueStyle style;
            try { style = DialogueCssParser.Parse(scene.DialogueCssOverride ?? project.DialogueUi.Css); }
            catch (DialogueCssException error) { style = DialogueCssParser.Parse(DialogueCssParser.DefaultCss); diagnostics.Add("CSS сцены: " + error.Message); }
            var background = project.Images.FirstOrDefault(i => i.Id == scene.BackgroundImageId);
            if (scene.BackgroundImageId is { } missing && background is null) diagnostics.Add($"Задник {missing} отсутствует в библиотеке.");
            var text = line?.Texts.GetValueOrDefault(language);
            if (line is not null && text is null)
            { text = line.Texts.GetValueOrDefault(project.DefaultLanguage, ""); diagnostics.Add($"Реплика: нет перевода «{language}», используется основной язык «{project.DefaultLanguage}»."); }
            var sprites = State.VisibleObjects.Select((id, slot) =>
            {
                var character = project.Characters.First(c => c.Id == id);
                var pose = character.States.First(s => s.Id == State.ObjectStates[id]);
                return new VisibleCharacter(id, character.Name, pose.Id, project.Images.First(i => i.Id == pose.ImageId), State.Placements.GetValueOrDefault(id), slot);
            }).OrderBy(s => s.Placement?.Layer ?? 0).ToImmutableArray();
            return new SceneFrame(line?.Id, line is null ? -1 : scene.Lines.IndexOf(line), text ?? "",
                line?.SpeakerId is { } speaker ? project.Characters.FirstOrDefault(c => c.Id == speaker)?.Name ?? "Неизвестный персонаж" : "",
                background, sprites, style, diagnostics.ToImmutable(), false, State.Wait?.Mode == "input") { Placements = State.Placements };
        }
    }

    private void Pump()
    {
        if (disposed || IsPaused || HasPendingLua || State.Error is not null || State.Completed) return;
        GameState? failedLuaState = null;
        try
        {
            passTime ??= System.Diagnostics.Stopwatch.StartNew();
            while (++passActions <= 2048)
            {
                if (State.Wait is not null || State.Choice is not null) { ResetPass(); return; }
                var before = State;
                ScenarioAction? action;
                if (!State.QueuedActions.IsEmpty)
                { action = State.QueuedActions[0]; State = State with { QueuedActions = State.QueuedActions.RemoveAt(0) }; }
                else
                {
                    if (State.NextActionId is not { } next) { State = State with { Completed = true }; ResetPass(); Audio(AudioEventKind.Stop); return; }
                    var actions = programs[State.SceneId];
                    var index = Find(actions, next);
                    if (index < 0) throw new InvalidOperationException($"Действие {next} отсутствует.");
                    action = actions[index];
                    State = State with { NextActionId = index + 1 < actions.Length ? actions[index + 1].Id : null };
                }
                if (action is ScriptAction or RunScriptAction) failedLuaState = before;
                if (action is ScriptAction or RunScriptAction && (++passLuaCalls > 32 || passTime.Elapsed.TotalSeconds > 5))
                    throw new InvalidOperationException("Превышен лимит действий Lua за проход (32 вызова / 5 секунд).");
                if (action is ScriptAction or RunScriptAction)
                {
                    var after = State;
                    State = before;
                    BeginLua(action, before, after);
                    return;
                }
                Execute(action);
                failedLuaState = null;
            }
            Fail("Превышен лимит действий без ожидания; возможен цикл переходов.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or OverflowException)
        { if (failedLuaState is not null) State = failedLuaState; Fail($"Сцена {State.SceneId}: {error.Message}"); }
    }

    private void Execute(ScenarioAction action)
    {
        switch (action)
        {
            case LineEntryAction: break;
            case TextAction a:
                if (!project.Scenes.First(s => s.Id == State.SceneId).Lines.Any(l => l.Id == a.LineId)) throw new InvalidOperationException($"Реплика {a.LineId} отсутствует.");
                State = State with { LineId = a.LineId, TextStartedAt = State.Time }; Audio(AudioEventKind.Line, a.LineId); break;
            case ShowAction a: Show(a.ObjectId); break;
            case HideAction a:
                if (!project.Characters.Any(c => c.Id == a.ObjectId)) Diagnostic($"Скрываемый персонаж {a.ObjectId} отсутствует.");
                State = State with { VisibleObjects = State.VisibleObjects.Remove(a.ObjectId) }; break;
            case StateAction a:
                if (a.ObjectId == Guid.Empty) { Diagnostic("состояние задано без говорящего."); break; }
                var character = project.Characters.FirstOrDefault(c => c.Id == a.ObjectId);
                if (character?.States.Any(s => s.Id == a.StateId) != true)
                { Diagnostic($"У персонажа {a.ObjectId} отсутствует состояние {a.StateId}."); State = State with { VisibleObjects = State.VisibleObjects.Remove(a.ObjectId), ObjectStates = State.ObjectStates.SetItem(a.ObjectId, a.StateId) }; }
                else { State = State with { ObjectStates = State.ObjectStates.SetItem(a.ObjectId, a.StateId) }; if (State.VisibleObjects.Contains(a.ObjectId)) Show(a.ObjectId); }
                break;
            case PlaceAction a:
                if (!project.Characters.Any(c => c.Id == a.ObjectId)) { Diagnostic($"Персонаж размещения {a.ObjectId} отсутствует."); break; }
                if (a.Placement is { IsValid: false }) throw new InvalidOperationException("Некорректное размещение.");
                State = State with { Placements = a.Placement is null ? State.Placements.Remove(a.ObjectId) : State.Placements.SetItem(a.ObjectId, a.Placement),
                    ActiveActions = State.ActiveActions.Where(m => m.ObjectId != a.ObjectId).ToImmutableArray() }; break;
            case MoveAction a:
                if (a.Target is not { IsValid: true } || !ScenarioProgram.Duration(a.Seconds) || !project.Characters.Any(c => c.Id == a.ObjectId)) throw new InvalidOperationException("Некорректное движение.");
                var from = Placement(a.ObjectId);
                State = State with { ActiveActions = State.ActiveActions.Where(m => m.ObjectId != a.ObjectId).ToImmutableArray() };
                if (a.Seconds == 0) State = State with { Placements = State.Placements.SetItem(a.ObjectId, a.Target) };
                else State = State with { ActiveActions = State.ActiveActions.Add(new(a.Id, a.ObjectId, from, a.Target, a.Seconds, 0)) }; break;
            case WaitAction a:
                if (a.Mode is not ("input" or "timer" or "actions" or "interaction") || !ScenarioProgram.Duration(a.Seconds)) throw new InvalidOperationException("Некорректное ожидание.");
                if (a.Mode == "interaction" && !State.Hotspots.Any(h => h.Enabled && (a.Token is null || h.ObjectId == a.Token))) throw new InvalidOperationException("Нет активного предмета для ожидания щелчка.");
                if (a.Mode == "timer" && a.Seconds == 0 || a.Mode == "actions" && State.ActiveActions.IsEmpty) break;
                State = State with { Wait = new(a.Id, a.Mode, a.Seconds, a.Token) }; break;
            case ChoiceAction a:
                if (a.Options.IsDefaultOrEmpty || a.Options.Length > 8 || a.Options.Any(o => o is null || o.Id == Guid.Empty || string.IsNullOrWhiteSpace(o.Text) ||
                    !programs.TryGetValue(o.TargetSceneId, out var choices) || Find(choices, o.TargetActionId) < 0) || a.Options.Select(o => o.Id).Distinct().Count() != a.Options.Length)
                    throw new InvalidOperationException("Некорректные варианты выбора или их переходы.");
                State = State with { Choice = a }; break;
            case JumpAction a: Jump(a.SceneId, a.ActionId); break;
            case BranchAction a: if (State.Variables.GetValueOrDefault(a.Variable) == a.EqualsValue) Jump(a.SceneId, a.ActionId); break;
            case SetAction a:
                if (string.IsNullOrWhiteSpace(a.Variable) || a.Value is null) throw new InvalidOperationException("Некорректная переменная.");
                SetVariable(a.Variable, a.Value); break;
            case HotspotAction a:
                if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || a.X < 0 || a.Y < 0 || a.Width <= 0 || a.Height <= 0 || !(a.X + a.Width <= 1) || !(a.Y + a.Height <= 1)) throw new InvalidOperationException("Некорректная область предмета.");
                State = State with { Hotspots = State.Hotspots.Where(h => h.ObjectId != a.ObjectId).ToImmutableArray().Add(a) }; break;
            default: throw new InvalidOperationException("Неизвестное действие.");
        }
    }

    private void Show(Guid id)
    {
        var character = project.Characters.FirstOrDefault(c => c.Id == id);
        if (character is null) { Diagnostic($"Персонаж {id} отсутствует."); return; }
        var stateId = State.ObjectStates.TryGetValue(id, out var remembered) ? remembered : character.DefaultStateId;
        var pose = character.States.FirstOrDefault(s => s.Id == stateId);
        if (pose is null) { Diagnostic($"У персонажа {id} отсутствует состояние {stateId}."); return; }
        if (!project.Images.Any(i => i.Id == pose.ImageId))
        { Diagnostic($"Изображение {pose.ImageId} отсутствует."); State = State with { VisibleObjects = State.VisibleObjects.Remove(id) }; return; }
        State = State with { ObjectStates = State.ObjectStates.SetItem(id, pose.Id),
            VisibleObjects = State.VisibleObjects.Contains(id) ? State.VisibleObjects : State.VisibleObjects.Add(id) };
    }

    private void SetVariable(string name, string value)
    {
        var variables = LuaLimits.SetVariable(State.Variables, ref variableCharacters, name, value);
        State = State with { Variables = variables };
    }

    private CharacterPlacement Placement(Guid id)
    {
        if (State.Placements.TryGetValue(id, out var placement)) return placement;
        var slot = State.VisibleObjects.IndexOf(id);
        var count = Math.Max(1, State.VisibleObjects.Length);
        // Match CharacterLayout's automatic bottom-center anchor.
        var x = (40 + (1200d / count) * (Math.Max(0, slot) + 0.5)) / 1280;
        var character = project.Characters.First(c => c.Id == id);
        var pose = character.States.FirstOrDefault(s => s.Id == State.ObjectStates.GetValueOrDefault(id)) ?? character.States.FirstOrDefault(s => s.Id == character.DefaultStateId);
        var image = project.Images.FirstOrDefault(i => i.Id == pose?.ImageId);
        var scale = image is null ? 1 : Math.Min(Math.Min(360, 1200d / count) / image.PixelWidth, 560d / image.PixelHeight) /
            Math.Min(360d / image.PixelWidth, 560d / image.PixelHeight);
        return new CharacterPlacement(x, 1, scale, 0);
    }

    private void Jump(Guid sceneId, Guid actionId)
    {
        if (!programs.TryGetValue(sceneId, out var actions) || Find(actions, actionId) < 0) throw new InvalidOperationException($"Переход {sceneId}/{actionId} отсутствует.");
        Audio(AudioEventKind.Jump);
        var changedScene = sceneId != State.SceneId;
        State = State with { SceneId = sceneId, NextActionId = actionId, QueuedActions = [], LineId = sceneId == State.SceneId ? State.LineId : null, Hotspots = [], Completed = false };
        if (changedScene) Audio(AudioEventKind.Scene);
    }

    public void Restore(GameState state)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        GameSave.Validate(state, project);
        CancelPendingLua();
        ResetPass();
        State = state;
        variableCharacters = state.Variables.Sum(v => v.Key.Length + v.Value.Length);
        audioEvents.Clear();
        Audio(state.Completed || state.Error is not null ? AudioEventKind.Stop : AudioEventKind.Restore);
    }

    private static int Find(ImmutableArray<ScenarioAction> actions, Guid id)
    { for (var i = 0; i < actions.Length; i++) if (actions[i].Id == id) return i; return -1; }
    private void Diagnostic(string message) => State = State with { Diagnostics = State.Diagnostics.Add(message) };
    private void Fail(string message) { State = State with { Error = message, Diagnostics = State.Diagnostics.Add(message) }; Audio(AudioEventKind.Stop); }
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
