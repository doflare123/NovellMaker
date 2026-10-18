using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;

namespace NovellMaker.Runtime;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.Out.WriteLine(RuntimeArguments.Usage);
            return 0;
        }

        try
        {
            var arguments = RuntimeArguments.Parse(args);
            var project = new JsonProjectStore().LoadAsync(arguments.ProjectPath).GetAwaiter().GetResult();
            var player = new ScenePlayer(project, arguments.SceneId, arguments.Language, arguments.LineId);
            using var game = new RuntimeGame(arguments, player, project.DialogueUi.ShowNavigationControls);
            game.Run();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Ошибка runtime: {exception.Message}");
            return 1;
        }
    }
}
