using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;
using System.Runtime.InteropServices;

namespace NovellMaker.Runtime;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);

    [STAThread]
    public static int Main(string[] args)
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
            if (arguments.Package is { } package && project.Id != package.ProjectId)
                throw new InvalidDataException("Манифест и проект относятся к разным играм.");
            var player = new ScenePlayer(project, arguments.SceneId, arguments.Language, arguments.LineId,
                arguments.Package is null ? PlaybackMode.AuthorPreview : PlaybackMode.Game);
            using var game = new RuntimeGame(arguments, player, project.DialogueUi.ShowNavigationControls);
            game.Run();
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
