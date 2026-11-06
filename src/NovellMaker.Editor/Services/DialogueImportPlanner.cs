using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Audio;
using NovellMaker.Core.Characters;
using NovellMaker.Core.Projects;
using NovellMaker.Core.Scenes;
using NovellMaker.Core.Styling;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.Services;

public sealed record ImportTarget(Guid? Id, bool Create, string Label)
{
    public override string ToString() => Label;
}
public sealed record DialogueImportOptions(string SourceKey, string Root, string Language, Guid? TargetSceneId,
    Guid? AfterLineId, bool UpdateExisting, bool IncludeVoices, bool IncludeImages,
    ImmutableDictionary<string, ImportTarget> Characters, DialogueImportProfile? Profile = null, string? ProjectDirectory = null);
public sealed record ImportResource(Guid Id, bool IsImage, string RelativeSource, string SourcePath, string Hash, bool NeedsCopy = true);
public sealed record DialogueImportPlan(NovelProject Original, NovelProject Draft, DialogueImportOptions Options,
    ImmutableArray<ImportResource> Resources, ImmutableArray<string> Changes, ImmutableArray<string> Errors,
    int NewLines, int UpdatedLines, int NewCharacters, int NewScenes, int VoiceCount, Guid? FirstSceneId, Guid? FirstLineId)
{
    public bool CanApply => Errors.IsEmpty && (Changes.Length > 0);
    public string Summary => $"Реплик: +{NewLines}, обновить {UpdatedLines}; персонажей: +{NewCharacters}; сцен: +{NewScenes}; WAV: {VoiceCount}. Ошибок: {Errors.Length}.";
}

public static class DialogueImportPlanner
{
    public static ImmutableArray<ImportCharacter> Characters(DialogueImportSource source)
    {
        if (source.Characters.IsDefault || source.Scenes.IsDefault || source.Lines.IsDefault) throw new InvalidDataException("Коллекции JSON импорта не заданы.");
        if (source.Characters.Any(c => c is null) || source.Scenes.Any(s => s is null || s.Lines.IsDefault) || source.Lines.Concat(source.Scenes.SelectMany(s => s.Lines)).Any(l => l is null))
            throw new InvalidDataException("JSON содержит пустую сцену или реплику.");
        var result = source.Characters.ToList();
        foreach (var line in source.Lines.Concat(source.Scenes.SelectMany(s => s.Lines)))
            if (!string.IsNullOrWhiteSpace(line.Speaker) && !result.Any(c => c.Key == line.Speaker)) result.Add(new(line.Speaker, line.Speaker));
        if (result.Any(c => c is null || string.IsNullOrWhiteSpace(c.Key) || string.IsNullOrWhiteSpace(c.Name)) || result.Select(c => c.Key).Distinct().Count() != result.Count)
            throw new InvalidDataException("Ключи персонажей должны быть непустыми и уникальными.");
        return result.ToImmutableArray();
    }
    public static ImportTarget? SuggestTarget(NovelProject project, string source, ImportCharacter character)
    {
        var bound = project.Imports.Bindings.FirstOrDefault(b => b.Source == source && b.Kind == ImportedObjectKind.Character && b.Key == character.Key);
        if (bound is { TargetId: null }) return new(null, false, "Рассказчик / без персонажа");
        if (bound is not null && project.Characters.FirstOrDefault(c => c.Id == bound.TargetId) is { } existing) return new(existing.Id, false, existing.Name);
        var names = project.Characters.Where(c => c.Name.Equals(character.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return names.Length switch { 0 => new(null, true, "Создать: " + character.Name), 1 => new(names[0].Id, false, names[0].Name), _ => null };
    }
    public static Task<DialogueImportPlan> PrepareAsync(NovelProject project, DialogueImportSource source, DialogueImportOptions options) => new Builder(project, source, options).BuildAsync();

    private sealed class Builder(NovelProject original, DialogueImportSource source, DialogueImportOptions options)
    {
        private readonly NovelProject baseline = original;
        private NovelProject draft = original;
        private readonly List<string> changes = [], errors = [];
        private readonly Dictionary<(bool, string), ImportResource> resources = new();
        private readonly Dictionary<string, Guid?> characterIds = new();
        private readonly Dictionary<(string, string), Guid> poses = new();
        private int added, updated, addedCharacters, addedScenes, voices;
        private Guid? firstScene, firstLine;
        private Guid? Bound(ImportedObjectKind kind, string? key) => options.UpdateExisting && !string.IsNullOrWhiteSpace(key)
            ? draft.Imports.Bindings.FirstOrDefault(b => b.Source == options.SourceKey && b.Kind == kind && b.Key == key)?.TargetId : null;
        private void Bind(ImportedObjectKind kind, string? key, Guid? id)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            var binding = new ImportObjectBinding(options.SourceKey, kind, key, id);
            var at = Array.FindIndex(draft.Imports.Bindings.ToArray(), b => b.Source == options.SourceKey && b.Kind == kind && b.Key == key);
            if (at >= 0 && draft.Imports.Bindings[at] == binding) return;
            draft = draft with { Imports = draft.Imports with { Bindings = at < 0 ? draft.Imports.Bindings.Add(binding) : draft.Imports.Bindings.SetItem(at, binding) } };
        }
        internal async Task<DialogueImportPlan> BuildAsync()
        {
            if (source.Version != 1) throw new InvalidDataException("Поддерживается JSON импорта version=1.");
            if (string.IsNullOrWhiteSpace(options.SourceKey) || string.IsNullOrWhiteSpace(options.Language)) throw new InvalidDataException("Укажите источник и язык импорта.");
            var characters = Characters(source);
            foreach (var imported in characters) await CharacterAsync(imported);
            var scenes = source.Scenes;
            if (!source.Lines.IsEmpty) scenes = scenes.Add(new() { Lines = source.Lines });
            var keys = new HashSet<string>(); var sceneKeys = new HashSet<string>();
            if (scenes.Sum(s => s.Lines.Length) > 20000) throw new InvalidDataException("В одном импорте допускается до 20000 реплик.");
            foreach (var imported in scenes)
            {
                if (imported is null || imported.Lines.IsDefault) throw new InvalidDataException("Некорректная сцена импорта.");
                if (imported.Key is not null && (!sceneKeys.Add(imported.Key) || string.IsNullOrWhiteSpace(imported.Key))) throw new InvalidDataException("Повторный или пустой ключ сцены.");
                var scene = Bound(ImportedObjectKind.Scene, imported.Key) is { } sceneId ? draft.Scenes.FirstOrDefault(s => s.Id == sceneId) :
                    imported.Key is null ? draft.Scenes.FirstOrDefault(s => s.Id == options.TargetSceneId) : null;
                if (scene is null)
                {
                    scene = NovelScene.Create(string.IsNullOrWhiteSpace(imported.Name) ? "Импорт диалога" : imported.Name); addedScenes++;
                    draft = draft with { Scenes = draft.Scenes.Add(scene) }; changes.Add("Создать сцену: " + scene.Name);
                }
                else if (!string.IsNullOrWhiteSpace(imported.Name) && imported.Name != scene.Name)
                { scene = scene with { Name = imported.Name }; changes.Add("Переименовать сцену: " + imported.Name); }
                if (scene.HasLegacyActionScript) throw new InvalidDataException("Импорт реплик недоступен для произвольного потока действий: " + scene.Name);
                Bind(ImportedObjectKind.Scene, imported.Key, scene.Id); firstScene ??= scene.Id;
                var insertion = scene.Id == options.TargetSceneId && options.AfterLineId is { } after ? Array.FindIndex(scene.Lines.ToArray(), l => l.Id == after) + 1 : scene.Lines.Length;
                foreach (var incoming in imported.Lines)
                {
                    var line = Normalize(incoming);
                    if (line.Key is not null && (!keys.Add(line.Key) || string.IsNullOrWhiteSpace(line.Key))) throw new InvalidDataException("Повторный или пустой ключ реплики: " + line.Key);
                    Guid? speaker = string.IsNullOrWhiteSpace(line.Speaker) ? null : characterIds[line.Speaker];
                    var bound = Bound(ImportedObjectKind.Line, line.Key);
                    if (bound is not null && !scene.Lines.Any(l => l.Id == bound)) throw new InvalidDataException("Реплика с ключом «" + line.Key + "» принадлежит другой сцене. Перенесите её в редакторе.");
                    var existing = scene.Lines.FirstOrDefault(l => l.Id == bound);
                    var replacement = (existing ?? DialogueLine.Create(options.Language)) with { Texts = (existing?.Texts ?? ImmutableDictionary<string, string>.Empty).SetItems(line.Texts), SpeakerId = speaker,
                        CharacterStateId = existing?.SpeakerId == speaker ? existing?.CharacterStateId : null };
                    if (!string.IsNullOrWhiteSpace(line.Pose) && speaker.HasValue)
                    {
                        if (poses.TryGetValue((line.Speaker!, line.Pose), out var pose)) replacement = replacement with { CharacterStateId = pose };
                        else if (options.IncludeImages) errors.Add("Неизвестная поза: " + line.Speaker + "/" + line.Pose);
                        else changes.Add("Поза пропущена: " + line.Pose);
                    }
                    if (existing is null) { scene = scene with { Lines = scene.Lines.Insert(insertion++, replacement) }; added++; }
                    else { scene = scene with { Lines = scene.Lines.Replace(existing, replacement) }; updated++; }
                    changes.Add($"{(existing is null ? "Добавить" : "Обновить")} фразу [{line.Key ?? "без ключа"}] · {draft.Characters.FirstOrDefault(c => c.Id == speaker)?.Name ?? "Рассказчик"}: {string.Join(" / ", line.Texts.Select(p => p.Key + ": " + p.Value))}");
                    Bind(ImportedObjectKind.Line, line.Key, replacement.Id);
                    if (firstLine is null) { firstLine = replacement.Id; firstScene = scene.Id; }
                    if (options.IncludeVoices) await VoiceAsync(line, replacement);
                }
                if (!scene.Lines.IsEmpty && !scene.Canvas.Nodes.Any(n => n.Dialogue != NovellMaker.Core.Screens.DialogueBinding.None))
                {
                    var nodes = DialogueCanvas.Add(scene.Canvas.Nodes, scene.Id, DialogueCssParser.Parse(scene.DialogueCssOverride ?? draft.DialogueUi.Css), legacy: true);
                    scene = scene with { Canvas = scene.Canvas with { Nodes = nodes } };
                }
                draft = draft with { Scenes = draft.Scenes.SetItem(Array.FindIndex(draft.Scenes.ToArray(), s => s.Id == scene.Id), scene) };
            }
            if (options.Profile is { } profile)
            {
                var at = Array.FindIndex(draft.Imports.Profiles.ToArray(), p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
                draft = draft with { Imports = draft.Imports with { Profiles = at < 0 ? draft.Imports.Profiles.Add(profile) : draft.Imports.Profiles.SetItem(at, profile) } };
                changes.Add("Сохранить профиль: " + profile.Name);
            }
            if (errors.Count == 0) draft.Validate();
            return new(baseline, draft, options, resources.Values.ToImmutableArray(), changes.ToImmutableArray(), errors.ToImmutableArray(), added, updated, addedCharacters, addedScenes, voices, firstScene, firstLine);
        }
        private ImportLine Normalize(ImportLine line)
        {
            if (line is null || line.Texts is null || line.Voices is null || line.Unvoiced is null) throw new InvalidDataException("Некорректная реплика JSON.");
            var language = line.Language ?? source.Language ?? options.Language;
            if (string.IsNullOrWhiteSpace(language)) throw new InvalidDataException("Пустой язык реплики.");
            if (line.Text is not null)
            {
                if (line.Texts.ContainsKey(language)) throw new InvalidDataException("Текст языка задан дважды.");
                line = line with { Texts = line.Texts.Add(language, line.Text) };
            }
            if (line.Voice is not null)
            {
                if (line.Voices.ContainsKey(language)) throw new InvalidDataException("WAV языка задан дважды.");
                line = line with { Voices = line.Voices.Add(language, line.Voice) };
            }
            if (line.Texts.IsEmpty || line.Texts.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null) ||
                line.Voices.Any(p => !line.Texts.ContainsKey(p.Key) || string.IsNullOrWhiteSpace(p.Value) || line.Unvoiced.Contains(p.Key)) ||
                line.Unvoiced.Any(l => !line.Texts.ContainsKey(l))) throw new InvalidDataException("Для голоса и отметок без озвучки нужен текст того же языка; конфликтующие отметки недопустимы.");
            return line;
        }
        private async Task CharacterAsync(ImportCharacter imported)
        {
            if (!options.Characters.TryGetValue(imported.Key, out var target)) throw new InvalidDataException("Сопоставьте персонажа: " + imported.Name);
            if (!target.Create && target.Id is null) { characterIds[imported.Key] = null; Bind(ImportedObjectKind.Character, imported.Key, null); return; }
            var character = target.Create ? NovelCharacter.Create(imported.Name) : draft.Characters.FirstOrDefault(c => c.Id == target.Id)
                ?? throw new InvalidDataException("Выбранный персонаж отсутствует.");
            if (target.Create) { addedCharacters++; draft = draft with { Characters = draft.Characters.Add(character) }; }
            var alreadyBound = baseline.Imports.Bindings.Any(b => b.Source == options.SourceKey && b.Kind == ImportedObjectKind.Character && b.Key == imported.Key && b.TargetId == character.Id);
            if (alreadyBound) character = character with { Name = imported.Name };
            characterIds[imported.Key] = character.Id;
            changes.Add((target.Create ? "Создать персонажа: " : "Использовать персонажа: ") + character.Name + (imported.Poses.IsEmpty && character.States.IsEmpty ? " (без графики)" : ""));
            if (imported.Poses.IsDefault || imported.Poses.Any(p => p is null || string.IsNullOrWhiteSpace(p.Key) || string.IsNullOrWhiteSpace(p.Name)) || imported.Poses.Select(p => p.Key).Distinct().Count() != imported.Poses.Length)
                throw new InvalidDataException("Некорректные позы персонажа: " + imported.Name);
            foreach (var pose in imported.Poses)
            {
                var poseKey = JsonSerializer.Serialize(new[] { imported.Key, pose.Key });
                var previous = character.States.FirstOrDefault(p => p.Id == Bound(ImportedObjectKind.Pose, poseKey));
                if (previous is null)
                {
                    var matching = character.States.Where(s => s.Name == pose.Name).ToArray();
                    if (matching.Length > 1) throw new InvalidDataException("Неоднозначная поза: " + pose.Name);
                    previous = matching.FirstOrDefault();
                }
                if (!options.IncludeImages) { if (previous is not null) poses[(imported.Key, pose.Key)] = previous.Id; continue; }
                if (previous?.Visual is not null) throw new InvalidDataException("Импорт статического изображения не заменяет слоистую/покадровую позу: " + character.Name + "/" + previous.Name);
                if (await ResourceAsync(pose.Image, true) is not { } image) continue;
                var replacement = previous is null ? new CharacterState(Guid.NewGuid(), pose.Name, image.Id) : previous with { Name = pose.Name, ImageId = image.Id, Visual = null };
                character = character with { States = previous is null ? character.States.Add(replacement) : character.States.Replace(previous, replacement), DefaultStateId = character.DefaultStateId ?? replacement.Id };
                poses[(imported.Key, pose.Key)] = replacement.Id; Bind(ImportedObjectKind.Pose, poseKey, replacement.Id);
                changes.Add((previous is null ? "Добавить позу: " : "Заменить изображение позы: ") + character.Name + "/" + pose.Name);
            }
            if (imported.DefaultPose is { } defaultPose && options.IncludeImages)
            {
                if (poses.TryGetValue((imported.Key, defaultPose), out var id)) character = character with { DefaultStateId = id };
                else if (errors.Count == 0) throw new InvalidDataException("Неизвестная исходная поза: " + defaultPose);
            }
            foreach (var state in character.States) poses.TryAdd((imported.Key, state.Name), state.Id);
            draft = draft with { Characters = draft.Characters.SetItem(Array.FindIndex(draft.Characters.ToArray(), c => c.Id == character.Id), character) };
            Bind(ImportedObjectKind.Character, imported.Key, character.Id);
        }
        private async Task VoiceAsync(ImportLine line, DialogueLine target)
        {
            var audio = draft.Audio;
            foreach (var language in line.Unvoiced)
            {
                var existing = audio.Voices.GetValueOrDefault(target.Id)?.Remove(language);
                audio = audio with { VoiceOmissions = audio.VoiceOmissions.SetItem(target.Id, (audio.VoiceOmissions.GetValueOrDefault(target.Id) ?? []).Add(language)),
                    Voices = existing is null || existing.IsEmpty ? audio.Voices.Remove(target.Id) : audio.Voices.SetItem(target.Id, existing) };
                changes.Add("Без озвучки: " + (line.Key ?? target.Id.ToString()) + " · " + language);
            }
            draft = draft with { Audio = audio };
            foreach (var voice in line.Voices)
            {
                if (await ResourceAsync(voice.Value, false) is not { } resource) continue;
                audio = draft.Audio;
                var existing = audio.Voices.GetValueOrDefault(target.Id) ?? ImmutableDictionary<string, Guid>.Empty;
                changes.Add((existing.ContainsKey(voice.Key) ? "Заменить WAV: " : "Назначить WAV: ") + voice.Value + " · " + voice.Key); voices++;
                draft = draft with { Audio = audio with { Voices = audio.Voices.SetItem(target.Id, existing.SetItem(voice.Key, resource.Id)),
                    VoiceOmissions = ViewModels.SceneDocumentViewModel.ClearOmission(audio, target.Id, voice.Key) } };
            }
        }
        private async Task<ImportResource?> ResourceAsync(string relative, bool image)
        {
            try
            {
                var path = ResolveSource(options.Root, relative);
                if (resources.TryGetValue((image, path), out var known)) return known;
                using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > (image ? ImageAssetService.MaximumCompressedBytes : 2L * 1024 * 1024 * 1024)) throw new InvalidDataException("Ресурс слишком велик.");
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input)); input.Position = 0;
                var kind = image ? ImportedObjectKind.Image : ImportedObjectKind.Audio;
                if (options.ProjectDirectory is { } projectRoot && Bound(kind, relative) is { } existingId)
                {
                    var existingPath = image ? draft.Images.FirstOrDefault(i => i.Id == existingId)?.RelativePath : draft.Audio.Assets.FirstOrDefault(a => a.Id == existingId)?.RelativePath;
                    if (existingPath is not null)
                    {
                        var full = image ? AssetPath.Resolve(projectRoot, existingPath) : AudioAssetPath.Resolve(projectRoot, existingPath);
                        if (File.Exists(full))
                        {
                            using var old = File.OpenRead(full);
                            if (Convert.ToHexString(await SHA256.HashDataAsync(old)) == hash)
                            {
                                var reused = new ImportResource(existingId, image, relative, path, hash, false);
                                resources[(image, path)] = reused; return reused;
                            }
                        }
                    }
                }
                Guid id;
                if (image)
                {
                    var asset = await ImageAssetService.InspectImportAsync(path); id = asset.Id;
                    draft = draft with { Images = draft.Images.Add(asset) };
                }
                else
                {
                    if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Для озвучки нужен PCM WAV.");
                    var wave = PcmWave.Inspect(input); id = Guid.NewGuid();
                    draft = draft with { Audio = draft.Audio with { Assets = draft.Audio.Assets.Add(new(id, Path.GetFileName(path), $"Assets/Audio/{id:N}.wav")) } };
                    changes.Add($"WAV {relative} · {wave.Length / (double)(wave.SampleRate * wave.Channels * 2):0.0} с");
                }
                input.Position = 0;
                if (Convert.ToHexString(await SHA256.HashDataAsync(input)) != hash) throw new IOException("Ресурс изменился при проверке.");
                var resource = new ImportResource(id, image, relative, path, hash);
                Bind(kind, relative, id);
                resources[(image, path)] = resource; return resource;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { errors.Add(relative + ": " + e.Message); return null; }
        }
    }
    internal static string ResolveSource(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) throw new InvalidDataException("Нужен относительный путь ресурса.");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => s is "" or "." or ".." || s.EndsWith(' ') || s.EndsWith('.') || s.IndexOfAny(['\0', '<', '>', '"', '|', '?', '*']) >= 0))
            throw new InvalidDataException("Некорректный относительный путь ресурса.");
        var current = Path.GetFullPath(root);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Ресурс проходит через символическую ссылку.");
        }
        return current;
    }
}
