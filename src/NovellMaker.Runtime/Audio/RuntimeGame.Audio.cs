using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Audio;
using NovellMaker.Runtime.Audio;
using NovellMaker.Core.Profiles;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private AudioService? audio;
    private readonly MonoGameAudioBackend audioBackend = new();
    private bool ShouldPauseAudio => screens.Stack.PausesScenario || player.Execution.HasPendingLua || player.Execution.IsPaused;
    private void InitializeAudio() => audio = new(player.Execution.Project, arguments.AssetsRoot, arguments.Language, audioBackend);
    private void ApplyPlayerSettings(PlayerSettings value)
    {
        value.Validate();
        if (graphics.IsFullScreen != value.Fullscreen)
        {
            var previous = graphics.IsFullScreen;
            try
            {
                graphics.IsFullScreen = value.Fullscreen;
                graphics.PreferredBackBufferWidth = value.Fullscreen ? GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width : LogicalWidth;
                graphics.PreferredBackBufferHeight = value.Fullscreen ? GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height : LogicalHeight;
                graphics.ApplyChanges();
            }
            catch (Exception error) when (error is InvalidOperationException or NoSuitableGraphicsDeviceException)
            {
                graphics.IsFullScreen = previous;
                graphics.PreferredBackBufferWidth = previous ? GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width : LogicalWidth;
                graphics.PreferredBackBufferHeight = previous ? GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height : LogicalHeight;
                try { graphics.ApplyChanges(); } catch (Exception rollback) when (rollback is InvalidOperationException or NoSuitableGraphicsDeviceException) { }
                throw new InvalidOperationException("Не удалось применить режим окна.", error);
            }
            screens.Stack.SetActive(false); inputActive = false;
        }
        foreach (var group in Enum.GetValues<AudioGroup>()) audio?.SetBus(group, value.Bus(group).ToBus());
        windowSettingsDiagnostic = "";
    }
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
                var updated = command.Action == NovellMaker.Core.Screens.ScreenButtonAction.Mute
                    ? bus with { Muted = !bus.Muted } : bus with { Volume = Math.Clamp(bus.Volume + (float)command.Amount, 0, 1) };
                audio.SetBus(command.Group, updated);
                settings.Set(settings.Current.SetBus(command.Group, updated));
                break;
            case NovellMaker.Core.Screens.ScreenButtonAction.Continue: ApplyAction(RuntimeAction.Next); break;
        }
    }
}
