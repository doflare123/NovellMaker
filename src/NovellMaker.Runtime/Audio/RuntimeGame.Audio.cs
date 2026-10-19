using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Audio;
using NovellMaker.Runtime.Audio;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private AudioService? audio;
    private readonly MonoGameAudioBackend audioBackend = new();
    private bool audioPaused;
    private bool showMixer;
    private readonly List<(AudioGroup Group, int Change, Rectangle Bounds, RenderedTexture Texture)> mixerButtons = [];
    private void InitializeAudio() => audio = new(player.Execution.Project, arguments.AssetsRoot, arguments.Language, audioBackend);
    private void SynchronizeAudio()
    {
        if (audio is null) return;
        var diagnosticCount = audio.Diagnostics.Count;
        foreach (var cue in player.DrainAudioEvents()) audio.Handle(cue);
        audio.Pause(audioPaused || showMixer);
        audio.Update();
        if (audio.Diagnostics.Count != diagnosticCount) RefreshTextTextures();
    }
    private void ToggleMixer() { showMixer = !showMixer; RefreshMixer(); }
    private void RefreshMixer()
    {
        if (audio is null) return;
        audio.Pause(audioPaused || showMixer);
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
    private bool ClickMixer(Point point)
    {
        var b = mixerButtons.FirstOrDefault(b => b.Bounds.Contains(point));
        if (b.Texture is null || b.Change == 0 || audio is null) return false;
        var bus = audio.Bus(b.Group);
        audio.SetBus(b.Group, b.Change == 2 ? bus with { Muted = !bus.Muted } : bus with { Volume = Math.Clamp(bus.Volume + b.Change * 0.1f, 0, 1) });
        RefreshMixer();
        return true;
    }
    private void DrawMixer(SpriteBatch batch)
    {
        if (!showMixer || pixel is null) return;
        batch.Draw(pixel, new Rectangle(140, 110, 1000, 340), new Color(20, 22, 26));
        foreach (var b in mixerButtons) batch.Draw(b.Texture.Texture, b.Bounds, Color.White);
    }
}
