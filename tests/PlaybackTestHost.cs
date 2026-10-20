using NovellMaker.Core.Execution;

// Characterization tests own a host loop. Waiting here never advances virtual game time.
// Production defaults remain asynchronous; pending behaviour has its own controlled-runner tests.
internal static class PlaybackTestHost
{
    public static ScenarioPlayer Settled(this ScenarioPlayer player)
    {
        for (var i = 0; player.PendingLuaTask is { } work; i++)
        {
            if (i >= 100) throw new InvalidOperationException("Test host exceeded pending Lua passes.");
            try { work.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or OperationCanceledException) { }
            player.PollLua();
        }
        return player;
    }
    public static ScenePlayer Settled(this ScenePlayer player) { player.Execution.Settled(); player.PollLua(); player.Tick(TimeSpan.Zero); return player; }
    public static void TickSettled(this ScenarioPlayer player, TimeSpan elapsed) { player.Tick(elapsed); player.Settled(); }
    public static void TickSettled(this ScenePlayer player, TimeSpan elapsed) { player.Tick(elapsed); player.Settled(); }
    public static bool AdvanceSettled(this ScenarioPlayer player) { var result = player.Advance(); player.Settled(); return result; }
    public static bool NextSettled(this ScenePlayer player) { var result = player.Next(); player.Settled(); return result; }
    public static bool ClickSettled(this ScenarioPlayer player, Guid id) { var result = player.Click(id); player.Settled(); return result; }
    public static bool ClickSettled(this ScenePlayer player, Guid id) { var result = player.Click(id); player.Settled(); return result; }
    public static bool ChooseSettled(this ScenarioPlayer player, Guid id) { var result = player.Choose(id); player.Settled(); return result; }
    public static bool ChooseSettled(this ScenePlayer player, Guid id) { var result = player.Choose(id); player.Settled(); return result; }
}
