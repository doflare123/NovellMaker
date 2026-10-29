using System.Collections.Immutable;

namespace NovellMaker.Core.Screens;

public sealed record ScreenPolicy(bool Modal = true, bool PauseScenario = true);
public sealed record ScreenInstance(Guid Id, Guid SessionId, Guid OwnerId, ScreenDefinition Definition, ScreenPolicy Policy)
{
    public ScreenDefinition Definition { get; internal set; } = Definition;
    public long Version { get; internal set; }
}
public enum ScreenInputKind { PointerDown, PointerUp, FocusNext, FocusPrevious, Activate, Back, PointerMove, KeyDown, KeyUp }
public readonly record struct ScreenInput(ScreenInputKind Kind, double X = 0, double Y = 0, string? Key = null, bool Shift = false, bool Control = false, bool Alt = false);
public sealed record ScreenActivation(Guid InstanceId, Guid NodeId);
public readonly record struct ScreenInputResult(bool Consumed, ScreenActivation? Activation = null);

/// <summary>Host-thread-only screen lifetime and input. No user code runs inside routing.</summary>
public sealed partial class ScreenStack
{
    public const int MaxInstances = 32;
    public const int MaxSessionNodes = 4096;
    private ImmutableArray<ScreenInstance> instances = [];
    private ScreenActivation? capture;
    private ScreenActivation? focus;
    private bool active = true;

    public ScreenStack(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Нужен ID сеанса.", nameof(sessionId));
        SessionId = sessionId;
    }

    public Guid SessionId { get; }
    /// <summary>Host-only read projection for rendering/input. Does not commit nodes or change Lua versions.</summary>
    public Func<ScreenInstance, ScreenDefinition, ScreenDefinition>? Presentation { get; set; }
    internal ScreenDefinition VisualDefinition(ScreenInstance instance, ScreenDefinition? source = null)
    {
        source ??= instance.Definition;
        var result = Presentation?.Invoke(instance, source) ?? source;
        if (result.Id != source.Id) throw new ArgumentException("Проекция принадлежит другому ресурсу.");
        return result;
    }
    public ImmutableArray<ScreenInstance> Instances => instances;
    public ScreenActivation? Focus => focus;
    public bool PausesScenario => instances.Any(i => i.Policy.PauseScenario);

    public ScreenInstance Open(Guid ownerId, ScreenDefinition definition, ScreenPolicy? policy = null)
    {
        var instance = Prepare(ownerId, definition, policy, null);
        instances = instances.Add(instance);
        ResetInput();
        return instance;
    }

    public ScreenInstance Replace(ScreenInstance current, ScreenDefinition definition, ScreenPolicy? policy = null)
    {
        var index = RequireOwned(current);
        var replacement = Prepare(current.OwnerId, definition, policy ?? current.Policy, index);
        instances = instances.SetItem(index, replacement);
        ResetInput();
        return replacement;
    }

    public void Close(ScreenInstance instance)
    {
        instances = instances.RemoveAt(RequireOwned(instance));
        ResetInput();
    }

    public void CloseOwner(Guid ownerId)
    {
        instances = [.. instances.Where(i => i.OwnerId != ownerId)];
        ResetInput();
    }

    /// <summary>Load/new game invalidates every old instance and pressed control.</summary>
    public void Clear() { instances = []; ResetInput(); }

    internal void Commit(ScreenInstance instance, long version, ScreenDefinition definition, bool animationFrame = false)
    {
        RequireOwned(instance);
        if (instance.Version != version) throw new InvalidOperationException("Устаревшая версия экрана.");
        if (definition.Id != instance.Definition.Id || instances.Sum(i => i.Definition.Nodes.Length) - instance.Definition.Nodes.Length + definition.Nodes.Length > MaxSessionNodes)
            throw new InvalidOperationException("Чужой ресурс или превышен бюджет узлов сеанса.");
        // Text updates must not swallow a press or reset focus on another screen.
        var buttons = VisualDefinition(instance, definition).Buttons;
        instance.Definition = definition;
        if (capture?.InstanceId == instance.Id && !buttons.Any(n => n.Id == capture.NodeId)) CancelPointer();
        if (focus?.InstanceId == instance.Id && !buttons.Any(n => n.Id == focus.NodeId)) ChangeFocus(null);
        if (hover?.InstanceId == instance.Id && !buttons.Any(n => n.Id == hover.NodeId)) ChangeHover(null);
        if (keys.Any(p => p.Value.InstanceId == instance.Id && !definition.Nodes.Any(n => n.Id == p.Value.NodeId))) ResetKeys(instance);
        if (!animationFrame) instance.Version++;
    }

    public void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        ResetInput();
    }

    public ScreenInputResult Route(ScreenInput input)
    {
        if (!Enum.IsDefined(input.Kind) || !double.IsFinite(input.X) || !double.IsFinite(input.Y) ||
            (input.Kind is ScreenInputKind.KeyDown or ScreenInputKind.KeyUp ? !ValidInputKey(input.Key) :
                input.Key is not null || input.Shift || input.Control || input.Alt))
            throw new ArgumentException("Недопустимое событие ввода.", nameof(input));
        if (!active) return new(true);
        if (input.Kind is ScreenInputKind.KeyDown or ScreenInputKind.KeyUp) return RouteKeyboard(input);
        if (input.Kind == ScreenInputKind.PointerMove) return MovePointer(input);
        if (input.Kind == ScreenInputKind.Back)
        {
            if (instances.IsEmpty) return new(false);
            Close(instances[^1]);
            return new(true);
        }
        if (input.Kind == ScreenInputKind.PointerUp && capture is { } pressed)
        {
            capture = null;
            var owner = instances.Single(i => i.Id == pressed.InstanceId);
            Emit(pressed, "pointer_up", input);
            if (dragging) Emit(pressed, "drag_end", input);
            var click = !dragging && VisualDefinition(owner).HitTest(input.X, input.Y) == pressed.NodeId;
            dragging = false;
            return new(true, click ? pressed : null);
        }
        if (input.Kind == ScreenInputKind.PointerDown) { CancelPointer(); PointerSequence++; }
        foreach (var instance in instances.Reverse())
        {
            var definition = VisualDefinition(instance);
            if (input.Kind is ScreenInputKind.PointerDown or ScreenInputKind.PointerUp)
            {
                if (definition.HitTest(input.X, input.Y) is { } node)
                {
                    if (input.Kind == ScreenInputKind.PointerDown)
                    {
                        capture = new(instance.Id, node); startX = input.X; startY = input.Y;
                        ChangeFocus(capture);
                        if (capture is { } target) Emit(target, "pointer_down", input);
                    }
                    return new(true);
                }
                if (definition.PointerTarget(input.X, input.Y) is not null) return new(true);
            }
            else
            {
                var buttons = definition.Buttons;
                if (!buttons.IsEmpty)
                {
                    var index = -1;
                    if (focus?.InstanceId == instance.Id)
                        for (var b = 0; b < buttons.Length; b++)
                            if (buttons[b].Id == focus.NodeId) index = b;
                    if (input.Kind == ScreenInputKind.Activate)
                    {
                        ChangeFocus(new(instance.Id, buttons[Math.Max(0, index)].Id));
                        return new(true, focus);
                    }
                    index = input.Kind == ScreenInputKind.FocusPrevious
                        ? (index <= 0 ? buttons.Length - 1 : index - 1) : (index + 1) % buttons.Length;
                    ChangeFocus(new(instance.Id, buttons[index].Id));
                    return new(true);
                }
            }
            if (instance.Policy.Modal) return new(true);
        }
        return new(false);
    }

    private ScreenInstance Prepare(Guid ownerId, ScreenDefinition definition, ScreenPolicy? policy, int? replaced)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (ownerId == Guid.Empty) throw new ArgumentException("Нужен ID владельца.", nameof(ownerId));
        var count = instances.Length + (replaced is null ? 1 : 0);
        var nodes = instances.Sum(i => i.Definition.Nodes.Length) + definition.Nodes.Length -
            (replaced is { } index ? instances[index].Definition.Nodes.Length : 0);
        if (count > MaxInstances || nodes > MaxSessionNodes)
            throw new InvalidOperationException("Превышен бюджет экранов сеанса.");
        return new(Guid.NewGuid(), SessionId, ownerId, definition, policy ?? new());
    }

    private int RequireOwned(ScreenInstance instance)
    {
        // Identity prevents a forged handle with the same IDs from changing another owner.
        var index = instances.IndexOf(instance);
        if (index < 0 || !ReferenceEquals(instances[index], instance))
            throw new InvalidOperationException("Экземпляр экрана закрыт или принадлежит другому стеку.");
        return index;
    }

    private void ResetInput()
    {
        var hadKeys = keys.Count != 0;
        InputEpoch++; CancelPointer(); ChangeFocus(null); ChangeHover(null); keys.Clear(); pointerKnown = false;
        if (hadKeys) foreach (var instance in instances)
            Emit(new(instance.Id, instance.Definition.Nodes.Single(n => n.ParentId is null).Id), "input_reset");
    }
}
