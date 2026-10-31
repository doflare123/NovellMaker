using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Execution;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private RenderedTexture? voiceLabel, voiceButton;
    private string voiceLabelKey = "", voiceButtonKey = "";
    private bool voicePointerHeld;
    private static Rectangle VoiceButtonBounds => new(688, 668, 272, 40);
    private bool ShowVoice => !screens.HasOverlays && session.Phase == GameSessionPhase.Playing;

    private bool ClickVoice(Point point)
    {
        if (!ShowVoice || !player.Execution.Project.DialogueUi.ShowVoiceControls || !VoiceButtonBounds.Contains(point)) return false;
        // Consume the disabled button too: it must never advance dialogue underneath.
        if (audio?.Voice is { Available: true } voice) audio.PauseVoice(!voice.Paused);
        return true;
    }

    private void DrawVoice(SpriteBatch batch)
    {
        if (!ShowVoice || pixel is null) return;
        var ui = player.Execution.Project.DialogueUi;
        var voice = audio?.Voice ?? VoicePlayback.None;
        if (ui.ShowVoiceProgress)
        {
            if (voiceLabelKey != voice.Label)
            {
                voiceLabelKey = voice.Label; voiceLabel?.Dispose();
                voiceLabel = DialogueTextureRenderer.RenderButton(GraphicsDevice, voice.Label, 648, 28);
            }
            if (voiceLabel is not null) batch.Draw(voiceLabel.Texture, new Rectangle(16, 668, 648, 28), Color.White);
            batch.Draw(pixel, new Rectangle(16, 700, 648, 6), new Color(60, 66, 76));
            var width = (int)(648 * voice.Progress);
            if (width > 0) batch.Draw(pixel, new Rectangle(16, 700, width, 6), new Color(161, 180, 204));
        }
        if (ui.ShowVoiceControls)
        {
            var label = !voice.Available ? "Нет озвучки" : voice.Paused ? "Продолжить озвучку" : "Пауза озвучки";
            if (voiceButtonKey != label)
            {
                voiceButtonKey = label; voiceButton?.Dispose();
                voiceButton = DialogueTextureRenderer.RenderButton(GraphicsDevice, label, VoiceButtonBounds.Width, VoiceButtonBounds.Height);
            }
            if (voiceButton is not null) batch.Draw(voiceButton.Texture, VoiceButtonBounds, voice.Available ? Color.White : Color.Gray);
        }
    }
}
