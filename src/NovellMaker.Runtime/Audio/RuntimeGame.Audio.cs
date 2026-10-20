using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Audio;
using NovellMaker.Runtime.Audio;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private AudioService? audio;
    private readonly MonoGameAudioBackend audioBackend = new();
    private bool showMixer => screens.ShowMixer;
    private bool ShouldPauseAudio => screens.Stack.PausesScenario || player.Execution.HasPendingLua || player.Execution.IsPaused;
    private readonly List<(AudioGroup Group, int Change, Rectangle Bounds, RenderedTexture Texture)> mixerButtons = [];
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
    private void ToggleMixer() { screens.ToggleMixer(); RefreshMixer(); }
    private void RefreshMixer()
    {
        if (audio is null) return;
        audio.Pause(ShouldPauseAudio);
        foreach (var b in mixerButtons) b.Texture.Dispose();
        mixerButtons.Clear();
        if (!showMixer) return;
        foreach (var group in Enum.GetValues<AudioGroup>())
        {
            var bus = audio.Bus(group);
            var y = 150 + (int)group * 70;
            var label = group switch { AudioGroup.Sound => "sound — музыка/окружение", AudioGroup.Effect => "effect — эффекты", AudioGroup.Voice => "voice — озвучка", _ => "Master" };
            Add(group, 0, 180, y, 520, $"{label}: {bus.Volume * 100:0}% · {(bus.Muted ? "выкл." : "вкл.")}");
            Add(group, -1, 710, y, 100, "−10%");
            Add(group, 1, 820, y, 100, "+10%");
            Add(group, 2, 930, y, 170, bus.Muted ? "Включить" : "Выключить");
        }
        void Add(AudioGroup g, int change, int x, int y, int w, string label) =>
            mixerButtons.Add((g, change, new Rectangle(x, y, w, 52), DialogueTextureRenderer.RenderButton(GraphicsDevice, label, w, 52)));
    }
    private void ChangeMixer(AudioGroup group, int change)
    {
        if (audio is null) return;
        var bus = audio.Bus(group);
        audio.SetBus(group, change == 2 ? bus with { Muted = !bus.Muted } : bus with { Volume = Math.Clamp(bus.Volume + change * 0.1f, 0, 1) });
        RefreshMixer();
    }
    private void DrawMixer(SpriteBatch batch)
    {
        if (!showMixer || pixel is null) return;
        batch.Draw(pixel, new Rectangle(140, 110, 1000, 340), new Color(20, 22, 26));
        foreach (var b in mixerButtons)
        {
            var focused = RuntimeScreens.MixerCommands.TryGetValue(screens.Stack.Focus?.NodeId ?? Guid.Empty, out var command) &&
                command.Group == b.Group && command.Change == b.Change;
            batch.Draw(b.Texture.Texture, b.Bounds, focused ? new Color(160, 215, 255) : Color.White);
        }
    }
}
