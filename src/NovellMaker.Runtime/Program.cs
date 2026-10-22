using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Profiles;
using System.Runtime.InteropServices;

namespace NovellMaker.Runtime;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--lua-worker") return LuaWorkerEntry.Run();
        if (args.Length == 1 && args[0] == "--screen-lua-worker") return NovellMaker.Core.Screens.Lua.ScreenLuaWorkerEntry.Run();
        return RunGame(args);
    }

    private static int RunGame(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.Out.WriteLine(RuntimeArguments.Usage);
            return 0;
        }

        Guid? packageId = null;
        try
        {
            var arguments = RuntimeArguments.Parse(args);
            packageId = arguments.Package?.ProjectId;
            var project = new JsonProjectStore().LoadAsync(arguments.ProjectPath).GetAwaiter().GetResult();
            arguments.Package?.ValidateProject(project);
            using var session = new GameSession(project, arguments.SceneId, arguments.Language, arguments.LineId,
                arguments.Package is null ? PlaybackMode.AuthorPreview : PlaybackMode.Game);
            var profile = new PlayerProfileStore(project.Id, RuntimeStorage.ProfilePath(project.Id, arguments.Package is not null));
            var settings = new PlayerSettingsSession(profile, profile.ReadAsync(PlayerSettings.Defaults(project.Audio)).GetAwaiter().GetResult());
            using var game = new RuntimeGame(arguments, session, project.DialogueUi.ShowNavigationControls, settings);
            try { game.Run(); }
            finally
            {
                settings.FlushAsync().GetAwaiter().GetResult();
                game.DisposeSlotsAsync().AsTask().GetAwaiter().GetResult();
                game.DisposeScreenLuaAsync().AsTask().GetAwaiter().GetResult();
                session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            } // Shutdown only, outside Update/Draw.
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ошибка runtime: {exception.Message}");
            string? report = null;
            try { report = RuntimeStorage.WriteStartupError(packageId, exception); Console.Error.WriteLine("Отчёт: " + report); }
            catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Не удалось записать отчёт: " + logError.Message); }
            if (OperatingSystem.IsWindows() && !args.Contains("--project") && !args.Contains("--smoke-output"))
                MessageBoxW(0, exception.Message + (report is null ? "" : "\n\nОтчёт: " + report), "Не удалось запустить игру", 0x10);
            return 1;
        }
    }
}
