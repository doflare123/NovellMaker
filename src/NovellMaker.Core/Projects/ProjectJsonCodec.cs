using System.Text.Json;
using NovellMaker.Core.Scenes;

namespace NovellMaker.Core.Projects;

internal static partial class ProjectJsonCodec
{
    internal static async Task<NovelProject> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var versionProbe = await JsonSerializer.DeserializeAsync<VersionProbe>(
            stream,
            VersionProbeSerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (versionProbe is null)
        {
            throw new ProjectFormatException("Файл проекта не содержит объекта проекта.");
        }

        if (versionProbe.FormatVersion > NovelProject.CurrentFormatVersion)
        {
            throw new ProjectFormatException(
                $"Версия формата проекта {versionProbe.FormatVersion} не поддерживается редактором. " +
                $"Поддерживается версия {NovelProject.CurrentFormatVersion}.");
        }

        stream.Position = 0;
        var project = versionProbe.FormatVersion switch
        {
            1 => MigrateFromVersion1(await DeserializeRequiredAsync<Version1ProjectDocument>(
                stream,
                cancellationToken).ConfigureAwait(false)),
            2 => MigrateFromVersion2(await DeserializeRequiredAsync<Version2ProjectDocument>(
                stream,
                cancellationToken).ConfigureAwait(false)),
            3 => MigrateFromVersion3(await DeserializeRequiredAsync<Version3ProjectDocument>(
                stream,
                cancellationToken).ConfigureAwait(false)),
            4 => MapVersion4(await DeserializeRequiredAsync<Version4ProjectDocument>(
                stream,
                cancellationToken).ConfigureAwait(false)),
            5 => MapVersion5(await DeserializeRequiredAsync<Version5ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
            6 => MapVersion6(await DeserializeRequiredAsync<Version6ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
            7 => MapVersion7(await DeserializeRequiredAsync<Version7ProjectDocument>(stream, cancellationToken, Version7SerializerOptions).ConfigureAwait(false)),
            9 => MapVersion9(await DeserializeRequiredAsync<Version9ProjectDocument>(stream, cancellationToken, PreVersion10SerializerOptions).ConfigureAwait(false)),
            11 => MapLegacyVersion11(await DeserializeRequiredAsync<Version11ProjectDocument>(stream, cancellationToken, Version11SerializerOptions).ConfigureAwait(false)),
            12 => MapVersion11(await DeserializeRequiredAsync<Version11ProjectDocument>(stream, cancellationToken, Version12SerializerOptions).ConfigureAwait(false)),
            13 => MapVersion13(await DeserializeRequiredAsync<Version13ProjectDocument>(stream, cancellationToken, Version13SerializerOptions).ConfigureAwait(false)),
            14 => MapVersion14(await DeserializeRequiredAsync<Version14ProjectDocument>(stream, cancellationToken, Version14SerializerOptions).ConfigureAwait(false)),
            15 => MapVersion14(await DeserializeRequiredAsync<Version14ProjectDocument>(stream, cancellationToken, Version15SerializerOptions).ConfigureAwait(false), false),
            16 => MapVersion14(await DeserializeRequiredAsync<Version14ProjectDocument>(stream, cancellationToken).ConfigureAwait(false), false),
            17 => MapVersion17(await DeserializeRequiredAsync<Version17ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
            18 => MapVersion17(await DeserializeRequiredAsync<Version17ProjectDocument>(stream, cancellationToken, Version18SerializerOptions).ConfigureAwait(false), allowSlots: true),
            19 => MapVersion17(await DeserializeRequiredAsync<Version17ProjectDocument>(stream, cancellationToken, CurrentSerializerOptions).ConfigureAwait(false), allowSlots: true),
            10 => MapVersion10(await DeserializeRequiredAsync<Version10ProjectDocument>(stream, cancellationToken).ConfigureAwait(false)),
            8 => MapVersion8(await DeserializeRequiredAsync<Version8ProjectDocument>(stream, cancellationToken, PreVersion10SerializerOptions).ConfigureAwait(false)),
            _ => throw new ProjectFormatException(
                $"Некорректная версия формата проекта: {versionProbe.FormatVersion}."),
        };
        if (versionProbe.FormatVersion < 16)
        {
            project = project with { Scenes = [.. project.Scenes.Select(scene =>
            {
                if (scene.Lines.IsEmpty && !scene.HasActionScript) return scene;
                NovellMaker.Core.Styling.DialogueStyle style;
                try { style = NovellMaker.Core.Styling.DialogueCssParser.Parse(scene.DialogueCssOverride ?? project.DialogueUi.Css); }
                catch (NovellMaker.Core.Styling.DialogueCssException) { style = NovellMaker.Core.Styling.DialogueCssParser.Parse(NovellMaker.Core.Styling.DialogueCssParser.DefaultCss); }
                return scene with { Canvas = scene.Canvas with { Nodes = DialogueCanvas.Add(scene.Canvas.Nodes, scene.Id, style, legacy: true, deterministic: true) } };
            })] };
        }
        return project;
    }
    internal static Task WriteAsync(Stream stream, NovelProject project, CancellationToken cancellationToken) =>
        JsonSerializer.SerializeAsync(stream, MapForSave(project), CurrentSerializerOptions, cancellationToken);
}
