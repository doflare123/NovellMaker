using NovellMaker.Core.Projects;

namespace NovellMaker.Editor.Services.Export;

public record WindowsGameOptions(string Version = "1.0.0", string? IconPath = null)
{
    public string ValidateVersion()
    {
        if (!System.Version.TryParse(Version, out var value) || value.Build < 0 ||
            new[] { value.Major, value.Minor, value.Build, Math.Max(0, value.Revision) }.Any(n => n > 65535))
            throw new ArgumentException("Версия игры: три или четыре числа от 0 до 65535, например 1.0.0.");
        return $"{value.Major}.{value.Minor}.{value.Build}.{Math.Max(0, value.Revision)}";
    }

    public void ValidateProjectVersion(NovelProject project)
    {
        var version = ValidateVersion();
        if (project.ReleaseCheck?.ToConfiguration() is { } release && version != release.CurrentVersion.ToString())
            throw new ArgumentException("Версия сборки должна совпадать с версией игры в параметрах проверки выпуска.");
    }
}
