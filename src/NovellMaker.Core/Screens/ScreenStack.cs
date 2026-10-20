using System.Collections.Immutable;

namespace NovellMaker.Core.Screens;

public sealed record ScreenPolicy(bool Modal = true, bool PauseScenario = true);
public sealed record ScreenInstance(Guid Id, Guid SessionId, Guid OwnerId, ScreenDefinition Definition, ScreenPolicy Policy);
public enum ScreenInputKind { PointerDown, PointerUp, FocusNext, FocusPrevious, Activate, Back }
public readonly record struct ScreenInput(ScreenInputKind Kind, double X = 0, double Y = 0);
public sealed record ScreenActivation(Guid InstanceId, Guid NodeId);
public readonly record struct ScreenInputResult(bool Consumed, ScreenActivation? Activation = null);

/// <summary>Host-thread-only screen lifetime and input. No user code runs inside routing.</summary>
public sealed class ScreenStack
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

    public void SetActive(bool value)
    {
        if (active == value) return;
        active = value;
        ResetInput();
    }

    public ScreenInputResult Route(ScreenInput input)
    {
        if (!Enum.IsDefined(input.Kind) || !double.IsFinite(input.X) || !double.IsFinite(input.Y))
            throw new ArgumentException("Недопустимое событие ввода.", nameof(input));
        if (!active) return new(true);
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
            return new(true, owner.Definition.HitTest(input.X, input.Y) == pressed.NodeId ? pressed : null);
        }
        if (input.Kind == ScreenInputKind.PointerDown) capture = null;
        foreach (var instance in instances.Reverse())
        {
            if (input.Kind is ScreenInputKind.PointerDown or ScreenInputKind.PointerUp)
            {
                if (instance.Definition.HitTest(input.X, input.Y) is { } node)
                {
                    focus = new(instance.Id, node);
                    if (input.Kind == ScreenInputKind.PointerDown) capture = focus;
                    return new(true);
                }
            }
            else
            {
                var buttons = instance.Definition.Buttons;
                if (!buttons.IsEmpty)
                {
                    var index = -1;
                    if (focus?.InstanceId == instance.Id)
                        for (var b = 0; b < buttons.Length; b++)
                            if (buttons[b].Id == focus.NodeId) index = b;
                    if (input.Kind == ScreenInputKind.Activate)
                    {
                        focus = new(instance.Id, buttons[Math.Max(0, index)].Id);
                        return new(true, focus);
                    }
                    index = input.Kind == ScreenInputKind.FocusPrevious
                        ? (index <= 0 ? buttons.Length - 1 : index - 1) : (index + 1) % buttons.Length;
                    focus = new(instance.Id, buttons[index].Id);
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

    private void ResetInput() { capture = null; focus = null; }
}
