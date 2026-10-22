using NovellMaker.Core.Projects;
using NovellMaker.Rendering;

namespace NovellMaker.Editor.Services;

/// <summary>Run/export constraints, deliberately separate from editable project persistence.</summary>
internal static class DialogueDisplayValidation
{
    internal static void Validate(NovelProject project)
    {
        foreach (var scene in project.Scenes)
            for (var index = 0; index < scene.Lines.Length; index++)
            {
                var line = scene.Lines[index];
                foreach (var (language, text) in line.Texts)
                    Check(text, $"Сцена «{scene.Name}», фраза {index + 1} ({line.Id}), язык «{language}»");
            }
        foreach (var character in project.Characters)
            Check(character.Name, $"Имя персонажа {character.Id}");
    }

    private static void Check(string text, string location)
    {
        if (text.Length > DialogueRasterizer.MaximumLayoutCharacters)
            throw new InvalidOperationException($"{location}: длина {text.Length} превышает лимит отображения " +
                $"{DialogueRasterizer.MaximumLayoutCharacters} единиц UTF-16 (включая разметку). " +
                "Разделите текст на несколько фраз или сократите его перед F6/экспортом. Исходный текст сохранён.");
    }
}
