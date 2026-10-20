using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NovellMaker.Core.Scenes;
using NovellMaker.Editor.ViewModels;

namespace NovellMaker.Editor.Views;

internal static class ScriptDragDrop
{
    internal sealed record Target(Guid SceneId, Guid? LineId, ScriptPhase Phase);
    internal static string Encode(Guid project, Guid script) => $"novellmaker-script:{project:D}:{script:D}";
    internal static Guid? Decode(string? text, EditorSession session)
    {
        var parts = text?.Split(':');
        return parts is { Length: 3 } && parts[0] == "novellmaker-script" && Guid.TryParse(parts[1], out var project) &&
            project == session.Project?.Id && Guid.TryParse(parts[2], out var script) && session.Project.Scripts.Any(s => s.Id == script) ? script : null;
    }
    internal static ExplorerEntry? Entry(object? source) => (source as Control)?.GetSelfAndVisualAncestors().OfType<Control>()
        .Select(c => c.DataContext switch { ExplorerEntry entry => entry, FolderTile tile => tile.Entry, _ => null }).FirstOrDefault(e => e is not null);

    public static void Source(Control host, Func<EditorSession?> session)
    {
        PointerPressedEventArgs? pressed = null; Point start = default; string? payload = null;
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            pressed = null; payload = null;
            if (e.GetCurrentPoint(host).Properties.IsLeftButtonPressed && Entry(e.Source)?.ScriptId is { } id && session()?.Project is { } project)
            { pressed = e; start = e.GetPosition(host); payload = Encode(project.Id, id); }
        }, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (pressed is null || payload is null || !e.GetCurrentPoint(host).Properties.IsLeftButtonPressed || Math.Abs(e.GetPosition(host).X - start.X) + Math.Abs(e.GetPosition(host).Y - start.Y) < 6) return;
            var gesture = pressed; var text = payload; pressed = null; payload = null;
            using var data = new DataTransfer(); data.Add(DataTransferItem.CreateText(text));
            try { await DragDrop.DoDragDropAsync(gesture, data, DragDropEffects.Copy); }
            catch (InvalidOperationException error) { session()?.Diagnostics.Insert(0, error.Message); }
        }, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, (_, _) => { pressed = null; payload = null; }, RoutingStrategies.Tunnel);
    }

    public static void Destination(Control host, Func<EditorSession?> session, Func<DragEventArgs, Target?> target)
    {
        DragDrop.SetAllowDrop(host, true);
        bool Valid(DragEventArgs e, out EditorSession? owner, out Guid? script, out Target? destination)
        {
            owner = session(); script = owner is null ? null : Decode(e.DataTransfer.TryGetText(), owner); destination = target(e);
            var location = destination;
            return owner?.CanEdit == true && script is not null && destination is not null &&
                owner.Project!.Scenes.Any(s => s.Id == location!.SceneId && !s.HasLegacyActionScript && (location.LineId is null || s.Lines.Any(l => l.Id == location.LineId)));
        }
        host.AddHandler(DragDrop.DragOverEvent, (sender, e) =>
        { e.DragEffects = Valid(e, out _, out _, out _) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; });
        host.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (Valid(e, out var owner, out var script, out var destination))
            { owner!.AttachScript(destination!.SceneId, destination.LineId, destination.Phase, script!.Value); e.Handled = true; }
        });
    }
}
