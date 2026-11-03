using NovellMaker.Core.Execution;
using NovellMaker.Core.Screens;

namespace NovellMaker.Runtime;

internal sealed partial class RuntimeGame
{
    private readonly ReadingPlayback reading;
    private readonly RuntimeHistoryServices historyServices;

    private void UpdateReading(TimeSpan elapsed, bool active)
    {
        historyServices.Update(active, screens.Stack.Instances.LastOrDefault());
        if (screens.HasOverlays || session.Phase != GameSessionPhase.Playing) reading.Stop();
        reading.Update(player, elapsed, active && session.Phase == GameSessionPhase.Playing && !screens.HasOverlays, audio?.IsVoicePlaying == true);
    }

    private bool ReadingAction(ScreenButtonAction action) =>
        session.Phase == GameSessionPhase.Playing && !screens.HasOverlays && reading.Toggle(player,
            action == ScreenButtonAction.AutoRead ? ReadingMode.Auto : ReadingMode.SkipRead);
}
