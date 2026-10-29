using System.Collections.Immutable;

namespace NovellMaker.Core.Screens;

public sealed record ScreenRoutedInput(ScreenInstance Instance, string Kind, Guid? NodeId,
    ScreenInput Input, long Epoch, long PointerSequence);
public sealed record ScreenInputCommand(string Kind, Guid? NodeId = null);

public sealed partial class ScreenStack
{
    private ScreenActivation? hover;
    private bool dragging;
    private double startX, startY;
    private bool pointerKnown;
    private double pointerX, pointerY;
    private readonly Dictionary<string, ScreenActivation> keys = new(StringComparer.Ordinal);
    public event Action<ScreenRoutedInput>? InputRouted;
    public long InputEpoch { get; private set; }
    public long PointerSequence { get; private set; }
    public ScreenActivation? Hover => hover;
    public ScreenActivation? PointerCapture => capture;
    public bool HasHeldInput(ScreenInstance instance) => instances.Any(i => ReferenceEquals(i, instance)) &&
        (capture?.InstanceId == instance.Id || keys.Values.Any(k => k.InstanceId == instance.Id));
    public void ReleaseInput(ScreenInstance instance)
    {
        if (!instances.Any(i => ReferenceEquals(i, instance))) return;
        ResetKeys(instance);
        if (capture?.InstanceId == instance.Id) CancelPointer();
        if (focus?.InstanceId == instance.Id) ChangeFocus(null);
        if (hover?.InstanceId == instance.Id) ChangeHover(null);
    }
    private void ResetKeys(ScreenInstance instance)
    {
        var held = keys.Where(p => p.Value.InstanceId == instance.Id).Select(p => p.Key).ToArray();
        foreach (var key in held) keys.Remove(key);
        if (held.Length != 0) Emit(new(instance.Id, instance.Definition.Nodes.Single(n => n.ParentId is null).Id), "input_reset");
    }

    private static readonly ImmutableHashSet<string> additionalKeys = ImmutableHashSet.Create(StringComparer.Ordinal,
        "Back", "Delete", "Home", "End", "PageUp", "PageDown",
        "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt");
    public static bool ValidInputKey(string? key) => ProjectInput.ValidKey(key) || key is not null && additionalKeys.Contains(key) ||
        key is { Length: 2 } && key[0] == 'D' && key[1] is >= '0' and <= '9';

    private bool Eligible(ScreenInstance instance) => active && instances.Any(i => ReferenceEquals(i, instance)) &&
        !instances.SkipWhile(i => !ReferenceEquals(i, instance)).Skip(1).Any(i => i.Policy.Modal);

    private void Emit(ScreenActivation target, string kind, ScreenInput input = default)
    {
        if (instances.FirstOrDefault(i => i.Id == target.InstanceId) is { } instance)
            InputRouted?.Invoke(new(instance, kind, target.NodeId, input, InputEpoch, PointerSequence));
    }
    private void ChangeFocus(ScreenActivation? value)
    {
        if (focus == value) return;
        var old = focus; focus = value;
        if (old is not null) Emit(old, "blur");
        if (value is not null) Emit(value, "focus");
    }
    private void ChangeHover(ScreenActivation? value, ScreenInput input = default)
    {
        if (hover == value) return;
        var old = hover; hover = value;
        if (old is not null) Emit(old, "hover_leave", input);
        if (value is not null) Emit(value, "hover_enter", input);
    }
    private void CancelPointer()
    {
        var old = capture; capture = null; dragging = false;
        if (old is not null) Emit(old, "pointer_cancel");
    }
    private ScreenInputResult MovePointer(ScreenInput input)
    {
        var moved = !pointerKnown || pointerX != input.X || pointerY != input.Y;
        pointerKnown = true; pointerX = input.X; pointerY = input.Y;
        ScreenActivation? hit = null;
        var consumed = false;
        foreach (var instance in instances.Reverse())
        {
            var definition = VisualDefinition(instance);
            if (definition.HitTest(input.X, input.Y) is { } node) hit = new(instance.Id, node);
            if (hit is not null || definition.PointerTarget(input.X, input.Y) is not null || instance.Policy.Modal)
            { consumed = true; break; }
        }
        ChangeHover(hit, input);
        if (!moved) return new(capture is not null || consumed);
        if (capture is { } pressed)
        {
            if (!dragging && Math.Abs(input.X - startX) + Math.Abs(input.Y - startY) >= 4)
            { dragging = true; Emit(pressed, "drag_start", input); }
            Emit(pressed, dragging ? "drag" : "move", input);
            return new(true);
        }
        if (hit is not null) Emit(hit, "move", input);
        return new(consumed);
    }
    private ScreenInputResult RouteKeyboard(ScreenInput input)
    {
        if (input.Kind == ScreenInputKind.KeyUp)
        {
            if (!keys.Remove(input.Key!, out var pressed)) return new(false);
            Emit(pressed, "key_up", input); return new(true);
        }
        if (keys.ContainsKey(input.Key!)) return new(false);
        if (focus is { } focused && instances.FirstOrDefault(i => i.Id == focused.InstanceId) is { } owner && Eligible(owner))
        { keys.Add(input.Key!, focused); Emit(focused, "key_down", input); return new(true); }
        foreach (var instance in instances.Reverse())
        {
            var target = new ScreenActivation(instance.Id, instance.Definition.Nodes.Single(n => n.ParentId is null).Id);
            keys.Add(input.Key!, target); Emit(target, "key_down", input);
            // Keys belong to one layer. The host decides whether a root without a handler consumes it.
            return new(instance.Policy.Modal);
        }
        return new(false);
    }

    /// <summary>Validate the entire command batch before publishing any effects.</summary>
    internal void ValidateInputCommands(ScreenDefinition definition, ImmutableArray<ScreenInputCommand> commands)
    {
        foreach (var command in commands)
            if (command.Kind is not ("capture_pointer" or "release_pointer" or "focus") ||
                (command.Kind == "release_pointer" ? command.NodeId is not null : !definition.Buttons.Any(n => n.Id == command.NodeId)))
                throw new InvalidOperationException("Команда ввода требует доступную кнопку или область.");
    }
    internal int InputEventBudget(ScreenInstance instance, ScreenDefinition definition, ImmutableArray<ScreenInputCommand> commands)
    {
        var buttons = VisualDefinition(instance, definition).Buttons;
        bool Removed(ScreenActivation? target) => target?.InstanceId == instance.Id && !buttons.Any(n => n.Id == target.NodeId);
        var nextFocus = focus; var nextCapture = capture; var count = 0;
        if (Removed(nextFocus)) { nextFocus = null; count++; }
        if (Removed(nextCapture)) { nextCapture = null; count++; }
        if (Removed(hover)) count++;
        if (keys.Values.Any(k => k.InstanceId == instance.Id && !definition.Nodes.Any(n => n.Id == k.NodeId))) count++;
        foreach (var command in commands)
        {
            if (command.Kind == "focus")
            {
                var next = new ScreenActivation(instance.Id, command.NodeId!.Value);
                if (nextFocus != next) { count += nextFocus is null ? 1 : 2; nextFocus = next; }
            }
            else if (command.Kind == "release_pointer" && nextCapture?.InstanceId == instance.Id)
            { nextCapture = null; count++; }
        }
        return count;
    }
    internal void ApplyInputCommands(ScreenInstance instance, ImmutableArray<ScreenInputCommand> commands,
        long epoch, long sequence)
    {
        // An async handler must never recover a press invalidated by a modal, load, release or focus loss.
        if (epoch != InputEpoch || !Eligible(instance)) return;
        foreach (var command in commands)
        {
            if (command.Kind == "focus") ChangeFocus(new(instance.Id, command.NodeId!.Value));
            else if (sequence == PointerSequence && capture?.InstanceId == instance.Id)
            {
                if (command.Kind == "release_pointer") CancelPointer();
                else capture = new(instance.Id, command.NodeId!.Value);
            }
        }
    }
}
