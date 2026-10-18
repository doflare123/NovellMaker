using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Execution;

public static class GameSave
{
    public const int CurrentVersion = 1;
    private sealed record Document([property: JsonRequired] int Version, [property: JsonRequired] GameState State);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };

    public static string Serialize(ScenarioPlayer player)
    {
        Validate(player.State, player.Project);
        return JsonSerializer.Serialize(new Document(CurrentVersion, player.State), Options);
    }

    public static void Load(ScenarioPlayer player, string json)
    {
        try
        {
            var document = JsonSerializer.Deserialize<Document>(json, Options) ?? throw new ArgumentException("Пустое сохранение.");
            if (document.Version != CurrentVersion) throw new ArgumentException($"Версия сохранения {document.Version} не поддерживается.");
            player.Restore(document.State);
        }
        catch (JsonException error) { throw new ArgumentException("Некорректный формат сохранения.", error); }
    }

    public static async Task SaveAsync(string path, ScenarioPlayer player, CancellationToken cancellationToken = default)
    {
        var json = Serialize(player);
        var full = Path.GetFullPath(path);
        var temporary = full + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(GameState state, NovelProject project)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ProjectId != project.Id || state.Revision != ScenarioProgram.Fingerprint(project))
            throw new ArgumentException("Сохранение относится к другому проекту или другой редакции сценария.");
        var scene = project.Scenes.FirstOrDefault(s => s.Id == state.SceneId) ?? throw new ArgumentException("Сцена сохранения отсутствует.");
        var program = ScenarioProgram.Compile(scene);
        bool KnownObject(Guid id) => project.Characters.Any(c => c.Id == id);
        if (state.NextActionId is { } next && !program.Any(a => a.Id == next) ||
            state.LineId is { } line && !scene.Lines.Any(l => l.Id == line) ||
            !double.IsFinite(state.Time) || state.Time < 0 || state.Time > TimeSpan.MaxValue.TotalSeconds || !double.IsFinite(state.TextStartedAt) || state.TextStartedAt < 0 || state.TextStartedAt > state.Time || state.Variables is null || state.ObjectStates is null || state.Placements is null ||
            state.VisibleObjects.IsDefault || state.ActiveActions.IsDefault || state.QueuedActions.IsDefault || state.Hotspots.IsDefault || state.Diagnostics.IsDefault)
            throw new ArgumentException("Некорректная позиция или коллекции сохранения.");
        if (state.Variables.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null) || state.Diagnostics.Any(d => d is null) ||
            state.Placements.Any(p => !KnownObject(p.Key) || p.Value is not { IsValid: true }) ||
            state.ObjectStates.Any(p => !project.Characters.Any(c => c.Id == p.Key && c.States.Any(s => s.Id == p.Value))) ||
            state.VisibleObjects.Distinct().Count() != state.VisibleObjects.Length || state.VisibleObjects.Any(id =>
                !state.ObjectStates.TryGetValue(id, out var pose) || !project.Characters.Any(c => c.Id == id && c.States.Any(s => s.Id == pose && project.Images.Any(i => i.Id == s.ImageId)))) ||
            state.ActiveActions.Any(a => a is null || a.ActionId == Guid.Empty || !KnownObject(a.ObjectId) || a.From is not { IsValid: true } || a.Target is not { IsValid: true } ||
                !ScenarioProgram.Duration(a.Duration) || a.Duration <= 0 || !double.IsFinite(a.Elapsed) || a.Elapsed < 0 || a.Elapsed >= a.Duration) ||
            state.ActiveActions.Select(a => a.ObjectId).Distinct().Count() != state.ActiveActions.Length)
            throw new ArgumentException("Некорректные объекты, переменные или активные действия сохранения.");
        if (state.Hotspots.Any(h => h is null) || state.QueuedActions.Any(a => a is null)) throw new ArgumentException("Сохранение содержит пустые команды.");
        if (state.Wait is { } wait && (wait.ActionId == Guid.Empty || wait.Mode is not ("input" or "timer" or "interaction" or "actions") ||
            wait.Mode == "actions" && state.ActiveActions.IsEmpty || wait.Mode == "timer" && wait.Remaining <= 0 ||
            !ScenarioProgram.Duration(wait.Remaining) || wait.Mode == "interaction" && !state.Hotspots.Any(h => (wait.Token is null || h.ObjectId == wait.Token) && h.Enabled)) ||
            state.Choice is not null && state.Wait is not null || state.Completed && (state.NextActionId is not null || state.Wait is not null || state.Choice is not null || !state.QueuedActions.IsEmpty))
            throw new ArgumentException("Некорректное ожидание сохранения.");
        // Validate all serialized executable data, including a Lua batch suspended at a wait.
        var additions = state.QueuedActions.Concat<ScenarioAction>(state.Hotspots);
        if (state.Choice is { } choice) additions = additions.Append(choice);
        foreach (var group in scene.Actions.Concat(additions).GroupBy(a => a.Id))
            if (group.Select(a => JsonSerializer.Serialize(a, Options)).Distinct(StringComparer.Ordinal).Skip(1).Any())
                throw new ArgumentException("Разные команды сохранения используют одинаковый ID.");
        ScenarioProgram.Validate(project);
        if (additions.Any(a => a.Id == Guid.Empty || !ScenarioProgram.IsValidAction(project, scene, a)))
            throw new ArgumentException("Некорректная команда в сохранении.");
    }
}
