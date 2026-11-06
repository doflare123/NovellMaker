using System.Collections.Immutable;

namespace NovellMaker.Core.Projects;

public enum ImportedObjectKind { Character, Scene, Line, Pose, Image, Audio }
public sealed record ImportObjectBinding(string Source, ImportedObjectKind Kind, string Key, Guid? TargetId);
public sealed record DialogueImportProfile(string Name, string Separator, string Language, ImmutableDictionary<string, string> Columns);

/// <summary>Authoring metadata only. Keys never replace the engine's stable IDs.</summary>
public sealed record AuthoringImports
{
    public ImmutableArray<ImportObjectBinding> Bindings { get; init; } = [];
    public ImmutableArray<DialogueImportProfile> Profiles { get; init; } = [];
    public static readonly ImmutableHashSet<string> ColumnFields = ["text", "speaker", "characterKey", "lineKey", "language", "voice", "image", "pose", "scene", "sceneKey"];
    public void Validate(NovelProject project)
    {
        if (Bindings.IsDefault || Profiles.IsDefault) throw new ArgumentException("Нет данных импорта.");
        var keys = new HashSet<(string, ImportedObjectKind, string)>();
        foreach (var binding in Bindings)
        {
            if (binding is null || string.IsNullOrWhiteSpace(binding.Source) || string.IsNullOrWhiteSpace(binding.Key) ||
                !keys.Add((binding.Source, binding.Kind, binding.Key)) || !Exists(project, binding))
                throw new ArgumentException("Некорректное соответствие внешнего ключа импорта.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Name) || !names.Add(profile.Name) || string.IsNullOrWhiteSpace(profile.Language) ||
                profile.Separator is not ("," or ";" or "\t") || profile.Columns is null ||
                profile.Columns.Any(p => !ColumnFields.Contains(p.Key) || string.IsNullOrWhiteSpace(p.Value)))
                throw new ArgumentException("Некорректный профиль импорта.");
        }
    }
    private static bool Exists(NovelProject p, ImportObjectBinding b) => b.Kind switch
    {
        ImportedObjectKind.Character => b.TargetId is null || p.Characters.Any(c => c.Id == b.TargetId),
        ImportedObjectKind.Scene => p.Scenes.Any(s => s.Id == b.TargetId),
        ImportedObjectKind.Line => p.Scenes.Any(s => s.Lines.Any(l => l.Id == b.TargetId)),
        ImportedObjectKind.Pose => p.Characters.Any(c => c.States.Any(s => s.Id == b.TargetId)),
        ImportedObjectKind.Image => p.Images.Any(i => i.Id == b.TargetId),
        ImportedObjectKind.Audio => p.Audio.Assets.Any(a => a.Id == b.TargetId),
        _ => false
    };
    public AuthoringImports Prune(NovelProject project) => this with { Bindings = Bindings.Where(b => Exists(project, b)).ToImmutableArray() };
    public bool ContentEquals(AuthoringImports other) => Bindings.SequenceEqual(other.Bindings) && Profiles.Length == other.Profiles.Length &&
        Profiles.Zip(other.Profiles).All(p => p.First.Name == p.Second.Name && p.First.Separator == p.Second.Separator && p.First.Language == p.Second.Language &&
            p.First.Columns.Count == p.Second.Columns.Count && p.First.Columns.All(v => p.Second.Columns.GetValueOrDefault(v.Key) == v.Value));
}
