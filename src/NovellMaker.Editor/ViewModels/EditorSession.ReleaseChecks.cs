using NovellMaker.Core.ExternalServices;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    public bool ReleaseCheckEnabled
    {
        get => project?.ReleaseCheck is not null;
        set
        {
            if (!CanEdit || project is null || value == ReleaseCheckEnabled) return;
            EditProject(project with { ReleaseCheck = value ? new(ReleaseCheckConfiguration.Provider, "", "", "1.0.0") : null });
        }
    }
    public string ReleaseCheckOwner
    {
        get => project?.ReleaseCheck?.Owner ?? "";
        set { if (CanEdit && project?.ReleaseCheck is { } settings) EditProject(project with { ReleaseCheck = settings with { Owner = value ?? "" } }, "release-owner"); }
    }
    public string ReleaseCheckRepository
    {
        get => project?.ReleaseCheck?.Repository ?? "";
        set { if (CanEdit && project?.ReleaseCheck is { } settings) EditProject(project with { ReleaseCheck = settings with { Repository = value ?? "" } }, "release-repository"); }
    }
    public string ReleaseCheckVersion
    {
        get => project?.ReleaseCheck?.CurrentVersion ?? "1.0.0";
        set { if (CanEdit && project?.ReleaseCheck is { } settings) EditProject(project with { ReleaseCheck = settings with { CurrentVersion = value ?? "" } }, "release-version"); }
    }
    public string ReleaseCheckValidation
    {
        get
        {
            try { _ = project?.ReleaseCheck?.ToConfiguration(); return ""; }
            catch (ArgumentException) { return "Проверка выпуска: укажите допустимые имя владельца и репозиторий GitHub, версию из 3–4 чисел 0–65535. Не вводите URL, токены или заголовки; известные префиксы токенов отклоняются."; }
        }
    }
    private void NotifyReleaseChecks()
    {
        foreach (var name in new[] { nameof(ReleaseCheckEnabled), nameof(ReleaseCheckOwner), nameof(ReleaseCheckRepository), nameof(ReleaseCheckVersion), nameof(ReleaseCheckValidation) }) Changed(name);
    }
}
