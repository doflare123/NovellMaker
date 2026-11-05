namespace NovellMaker.Core.Execution;

/// <summary>Completion barrier shared by playback and save validation. No additional clock or save data.</summary>
internal static class ScenarioActionTiming
{
    public static double Remaining(GameState state, Projects.NovelProject project)
    {
        var remaining = state.ActiveActions.Select(a => a.Duration - a.Elapsed).DefaultIfEmpty(0).Max();
        // Only explicit clip launches are actions. Pose defaults may be ambient loops.
        foreach (var (objectId, clipId) in state.CharacterClips)
        {
            var pose = project.Characters.First(c => c.Id == objectId).States.First(s => s.Id == state.ObjectStates[objectId]);
            var clip = pose.Visual!.Clips.First(c => c.Id == clipId);
            if (clip.Loop && clip.RepeatCount == 0) continue;
            var duration = clip.Duration * (clip.Loop ? clip.RepeatCount + 1 : 1);
            remaining = Math.Max(remaining, state.CharacterStartedAt[objectId] + duration - state.Time);
        }
        return Math.Max(0, remaining);
    }
}
