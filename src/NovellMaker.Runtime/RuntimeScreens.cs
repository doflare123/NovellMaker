using System.Collections.Immutable;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Screens;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Runtime;

/// <summary>Engine-provided menus until author-defined screen resources arrive in stage 2.3.</summary>
internal sealed class RuntimeScreens : IDisposable, IAsyncDisposable
{
    private readonly Guid ownerId;
    private readonly string? pauseHandler;
    public RuntimeScreens(Guid ownerId, bool enableLua = false, string? pauseHandler = null)
    {
        this.ownerId = ownerId;
        this.pauseHandler = pauseHandler;
        if (enableLua) Scripts = new(Stack);
    }
    public ScreenLuaSession? Scripts { get; }
    public bool HasPending => Scripts?.HasPending ?? false;
    public bool Poll() => Scripts?.Poll() ?? false;
    internal static readonly Guid PauseId = Id(1);
    internal static readonly Guid MixerId = Id(2);
    internal static readonly Guid ContinueId = Id(3);
    internal static readonly Guid SoundId = Id(4);
    internal static readonly ScreenRect PauseBounds = new(360, 200, 560, 300);
    internal static readonly ScreenDefinition Pause = new(PauseId,
    [
        new(Id(5), null, ScreenNodeKind.Panel, PauseBounds),
        new(Id(6), Id(5), ScreenNodeKind.Text, new(40, 24, 480, 50), "Пауза"),
        new(ContinueId, Id(5), ScreenNodeKind.Button, new(40, 100, 480, 52), "Продолжить"),
        new(SoundId, Id(5), ScreenNodeKind.Button, new(40, 170, 480, 52), "Звук"),
    ]);
    internal static readonly ImmutableDictionary<Guid, (AudioGroup Group, int Change)> MixerCommands =
        Enum.GetValues<AudioGroup>().SelectMany(g => new[] { -1, 1, 2 }.Select(change =>
            KeyValuePair.Create(Id(100 + (int)g * 10 + change + 1), (g, change)))).ToImmutableDictionary();
    internal static readonly ScreenDefinition Mixer = CreateMixer();

    public ScreenStack Stack { get; } = new(Guid.NewGuid());
    public bool ShowMixer => Stack.Instances.Any(i => i.Definition.Id == MixerId);

    public void TogglePause()
    {
        if (Stack.Instances.IsEmpty)
        {
            var instance = Stack.Open(ownerId, Pause);
            Scripts?.Attach(instance, pauseHandler ?? """
                return {
                  on_load = function() ui.set_text(refs.title, "Пауза · Esc — назад") end,
                  on_event = function(event)
                    if event.node == refs.continue_button then ui.close() end
                  end
                }
                """, ImmutableDictionary<string, Guid>.Empty.Add("title", Id(6)).Add("continue_button", ContinueId));
        }
        else Stack.Route(new(ScreenInputKind.Back));
    }

    public void ToggleMixer()
    {
        if (Stack.Instances.LastOrDefault() is { } top && top.Definition.Id == MixerId) Stack.Close(top);
        else Stack.Open(ownerId, Mixer);
    }

    public ScreenInputResult Route(ScreenInput input)
    {
        var result = Stack.Route(input);
        if (result.Activation is { } activation)
        {
            var instance = Stack.Instances.Single(i => i.Id == activation.InstanceId);
            var accepted = Scripts?.EnqueueUsingInstanceSnapshot(instance, new("click", activation.NodeId)) is ScreenEventAdmission.Accepted or ScreenEventAdmission.Coalesced;
            if (activation.NodeId == ContinueId)
            {
                if (!accepted) Stack.Close(instance); // Engine fallback remains usable when the handler fails.
            }
            else if (activation.NodeId == SoundId) ToggleMixer();
        }
        return result;
    }

    public void Reset() { Stack.Clear(); Scripts?.Reset(); }
    public void Dispose() { Stack.Clear(); Scripts?.Dispose(); }
    public async ValueTask DisposeAsync() { Dispose(); if (Scripts is { } scripts) await scripts.DisposeAsync(); }

    private static ScreenDefinition CreateMixer()
    {
        var root = Id(7);
        var nodes = ImmutableArray.CreateBuilder<ScreenNode>();
        nodes.Add(new(root, null, ScreenNodeKind.Panel, new(140, 110, 1000, 340)));
        foreach (var (id, command) in MixerCommands.OrderBy(c => c.Value.Group).ThenBy(c => c.Value.Change))
        {
            var x = command.Change switch { -1 => 570, 1 => 680, _ => 790 };
            nodes.Add(new(id, root, ScreenNodeKind.Button,
                new(x, 40 + (int)command.Group * 70, command.Change == 2 ? 170 : 100, 52)));
        }
        return new(MixerId, nodes.ToImmutable());
    }

    private static Guid Id(int value) => new(value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
}
