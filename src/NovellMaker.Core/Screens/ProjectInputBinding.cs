using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Screens;

/// <summary>Project-owned keyboard mapping; the engine has no pause, mixer or save shortcuts.</summary>
public sealed record ProjectInputBinding(string Key, ScreenButtonAction Action, Guid? TargetSceneId);

public static class ProjectInput
{
    public static bool ValidKey(string? key) => key is not null &&
        (key.Length == 1 && key[0] is >= 'A' and <= 'Z' ||
         key is "Escape" or "Enter" or "Space" or "Tab" or "Left" or "Right" or "Up" or "Down" ||
         key.StartsWith('F') && int.TryParse(key.AsSpan(1), out var number) && number is >= 1 and <= 12 && key == "F" + number);

    public static void Validate(NovelProject project)
    {
        if (project.InputBindings.IsDefault || project.InputBindings.Length > 64 ||
            project.InputBindings.Any(b => b is null || !ValidKey(b.Key) ||
                b.Action is not (ScreenButtonAction.Open or ScreenButtonAction.Toggle or ScreenButtonAction.Scene or
                    ScreenButtonAction.Back or ScreenButtonAction.Continue or ScreenButtonAction.Save or ScreenButtonAction.Load) ||
                (b.Action is ScreenButtonAction.Open or ScreenButtonAction.Toggle or ScreenButtonAction.Scene
                    ? !project.Scenes.Any(s => s.Id == b.TargetSceneId) : b.TargetSceneId is not null)) ||
            project.InputBindings.Select(b => b.Key).Distinct(StringComparer.Ordinal).Count() != project.InputBindings.Length)
            throw new ArgumentException("Некорректные назначения клавиш проекта (до 64 уникальных клавиш). ");
    }
}
