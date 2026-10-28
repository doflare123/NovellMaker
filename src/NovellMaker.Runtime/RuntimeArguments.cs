using System.Globalization;
using NovellMaker.Core.Projects;

namespace NovellMaker.Runtime;

internal sealed record RuntimeArguments(
    string ProjectPath,
    string AssetsRoot,
    Guid SceneId,
    Guid? LineId,
    string Language,
    string? SmokeOutputPath,
    string? SmokeReportPath)
{
    public string? SmokePlanPath { get; init; }
    public TimeSpan? SmokeRealtimeDuration { get; init; }
    public GamePackage? Package { get; init; }
    public static RuntimeArguments Parse(IReadOnlyList<string> arguments, string? packageDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var option = arguments[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Count)
            {
                throw new ArgumentException($"Ожидалась пара --параметр значение около «{option}».\n{Usage}");
            }

            if (option is not ("--project" or "--assets-root" or "--scene" or "--line" or
                "--language" or "--smoke-output" or "--smoke-report" or "--smoke-plan" or "--smoke-realtime-seconds"))
            {
                throw new ArgumentException($"Неизвестный параметр «{option}».\n{Usage}");
            }

            if (!values.TryAdd(option, arguments[index + 1]))
            {
                throw new ArgumentException($"Параметр «{option}» указан более одного раза.");
            }
        }

        GamePackage? package = null;
        if (!values.ContainsKey("--project"))
        {
            var directory = Path.GetFullPath(packageDirectory ?? AppContext.BaseDirectory);
            package = GamePackage.Load(directory);
            if (values.Keys.Any(k => k is not ("--smoke-output" or "--smoke-report" or "--smoke-plan" or "--smoke-realtime-seconds")))
                throw new ArgumentException("Упакованная игра использует стартовую сцену и язык из game.json.");
            values.Add("--project", Path.Combine(directory, GamePackage.ProjectName));
            values.Add("--assets-root", directory);
            values.Add("--scene", package.StartSceneId.ToString());
            values.Add("--language", package.Language);
        }
        var projectPath = RequiredPath(values, "--project", requireFile: true);
        var assetsRoot = RequiredPath(values, "--assets-root", requireFile: false);
        var sceneId = RequiredGuid(values, "--scene");
        var lineId = OptionalGuid(values, "--line");
        var language = Required(values, "--language").Trim();
        if (language.Length == 0)
        {
            throw new ArgumentException("Код языка не может быть пустым.");
        }

        var smokeOutput = OptionalOutputPath(values, "--smoke-output");
        var smokeReport = OptionalOutputPath(values, "--smoke-report");
        if ((smokeOutput is null) != (smokeReport is null))
        {
            throw new ArgumentException("Для smoke-проверки нужно одновременно указать --smoke-output и --smoke-report.");
        }

        if (values.ContainsKey("--smoke-plan") && smokeOutput is null) throw new ArgumentException("--smoke-plan требует smoke-output/report.");
        TimeSpan? realtime = null;
        if (values.TryGetValue("--smoke-realtime-seconds", out var duration))
        {
            if (smokeOutput is null || values.ContainsKey("--smoke-plan") ||
                !double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds is < 1 or > 30)
                throw new ArgumentException("--smoke-realtime-seconds требует smoke-output/report, 1–30 секунд и отсутствие smoke-plan.");
            realtime = TimeSpan.FromSeconds(seconds);
        }
        return new RuntimeArguments(
            projectPath,
            assetsRoot,
            sceneId,
            lineId,
            language,
            smokeOutput,
            smokeReport) { Package = package, SmokePlanPath = values.ContainsKey("--smoke-plan") ? RequiredPath(values, "--smoke-plan", true) : null,
                SmokeRealtimeDuration = realtime };
    }

    public static string Usage =>
        "Использование: NovellMaker.Runtime --project <snapshot.novell> --assets-root <каталог> " +
        "--scene <guid> [--line <guid>] --language <tag> " +
        "[--smoke-output <image.png> --smoke-report <report.json>]";

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string option)
    {
        if (!values.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Не указан обязательный параметр {option}.\n{Usage}");
        }

        return value;
    }

    private static string RequiredPath(
        IReadOnlyDictionary<string, string> values,
        string option,
        bool requireFile)
    {
        var path = Path.GetFullPath(Required(values, option));
        if (requireFile ? !File.Exists(path) : !Directory.Exists(path))
        {
            var description = requireFile ? "Файл" : "Каталог";
            throw new ArgumentException($"{description} для {option} не найден: {path}");
        }

        return path;
    }

    private static string? OptionalOutputPath(
        IReadOnlyDictionary<string, string> values,
        string option) =>
        values.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : null;

    private static Guid RequiredGuid(
        IReadOnlyDictionary<string, string> values,
        string option)
    {
        var value = Required(values, option);
        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Параметр {0} должен содержать непустой GUID.",
                    option));
        }

        return result;
    }

    private static Guid? OptionalGuid(
        IReadOnlyDictionary<string, string> values,
        string option)
    {
        if (!values.TryGetValue(option, out var value))
        {
            return null;
        }

        if (!Guid.TryParse(value, out var result) || result == Guid.Empty)
        {
            throw new ArgumentException($"Параметр {option} должен содержать непустой GUID.");
        }

        return result;
    }
}
