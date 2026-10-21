using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Audio;
using NovellMaker.Runtime.Audio;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private AudioService? audio;
    private readonly MonoGameAudioBackend audioBackend = new();
    private bool ShouldPauseAudio => screens.Stack.PausesScenario || player.Execution.HasPendingLua || player.Execution.IsPaused;
    private void InitializeAudio() => audio = new(player.Execution.Project, arguments.AssetsRoot, arguments.Language, audioBackend);
    private void SynchronizeAudio()
    {
        if (audio is null) return;
        var diagnosticCount = audio.Diagnostics.Count;
        audio.Pause(ShouldPauseAudio);
        foreach (var cue in player.DrainAudioEvents()) audio.Handle(cue);
        audio.Update();
        if (audio.Diagnostics.Count != diagnosticCount) RefreshTextTextures();
    }
    private void ApplyProjectCommand(NovellMaker.Core.Screens.ScreenButtonBinding command)
    {
        switch (command.Action)
        {
            case NovellMaker.Core.Screens.ScreenButtonAction.Volume:
            case NovellMaker.Core.Screens.ScreenButtonAction.Mute:
                if (audio is null) return;
                var bus = audio.Bus(command.Group);
                audio.SetBus(command.Group, command.Action == NovellMaker.Core.Screens.ScreenButtonAction.Mute
                    ? bus with { Muted = !bus.Muted } : bus with { Volume = Math.Clamp(bus.Volume + (float)command.Amount, 0, 1) });
                break;
            case NovellMaker.Core.Screens.ScreenButtonAction.Continue: ApplyAction(RuntimeAction.Next); break;
            case NovellMaker.Core.Screens.ScreenButtonAction.Save: SaveGame(); break;
            case NovellMaker.Core.Screens.ScreenButtonAction.Load: LoadGame(); break;
        }
    }
}
