using System.Collections.Immutable;
using NovellMaker.Core.Execution;
using NovellMaker.Core.Projects;

namespace NovellMaker.Core.Scripting;

/// <summary>Immutable source snapshot. Graph preparation runs after admission, outside the host thread.</summary>
public sealed class LuaModuleLibrary(Guid rootId, ImmutableArray<LuaScriptAsset> scripts)
{
    internal LuaModuleBundle Prepare(string source, Action checkpoint)
    {
        if (rootId == Guid.Empty) throw new ArgumentException("Некорректный каталог Lua-модулей.");
        return Prepare(source, checkpoint, BuildIndex(scripts, checkpoint));
    }

    private static Dictionary<Guid, LuaScriptAsset> BuildIndex(ImmutableArray<LuaScriptAsset> scripts, Action checkpoint)
    {
        checkpoint();
        if (scripts.IsDefault) throw new ArgumentException("Некорректный каталог Lua-модулей.");
        var index = new Dictionary<Guid, LuaScriptAsset>();
        foreach (var script in scripts)
        {
            checkpoint();
            if (script is null || script.Id == Guid.Empty || !index.TryAdd(script.Id, script))
                throw new ArgumentException("Повторный или отсутствующий ID Lua-ресурса.");
        }
        return index;
    }

    private LuaModuleBundle Prepare(string source, Action checkpoint, Dictionary<Guid, LuaScriptAsset> index)
    {
        if (!index.TryGetValue(rootId, out var root) || root.Source != source)
            throw new ArgumentException("Корневой Lua-ресурс не соответствует снимку кода.");
        var selected = new Dictionary<Guid, LuaModuleSource>();
        void Gather(LuaScriptAsset script)
        {
            checkpoint();
            if (selected.ContainsKey(script.Id)) return;
            if (selected.Count >= LuaModuleBundle.MaximumResources) throw new ArgumentException("Превышен бюджет Lua-модулей.");
            script.Validate();
            var imports = ImmutableDictionary.CreateBuilder<string, Guid>(StringComparer.Ordinal);
            foreach (var pair in script.References)
            {
                checkpoint();
                if (index.ContainsKey(pair.Value)) imports.Add(pair.Key, pair.Value);
            }
            var entry = new LuaModuleSource(script.Id, script.Source, imports.ToImmutable());
            selected.Add(script.Id, entry);
            foreach (var id in imports.Values.Distinct()) Gather(index[id]);
        }
        Gather(root);
        var bundle = new LuaModuleBundle(rootId, selected[rootId].Imports,
            selected.Values.Where(s => s.Id != rootId).OrderBy(s => s.Id).ToImmutableArray());
        _ = bundle.Validate(source, checkpoint);
        return bundle;
    }

    /// <summary>Preflight only; does not parse with MoonSharp or execute author code.</summary>
    public static void ValidateProject(NovelProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var index = BuildIndex(project.Scripts, cancellationToken.ThrowIfCancellationRequested);
        foreach (var id in project.Scenes.Select(s => s.Canvas.HandlerScriptId).OfType<Guid>().Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!index.TryGetValue(id, out var handler)) throw new ArgumentException("Отсутствует Lua-обработчик сцены.");
            _ = new LuaModuleLibrary(id, project.Scripts).Prepare(handler.Source, cancellationToken.ThrowIfCancellationRequested, index);
        }
    }
}

internal sealed record LuaModuleSource(Guid Id, string Source, ImmutableDictionary<string, Guid> Imports);
internal sealed record LuaModuleBundle(Guid RootId, ImmutableDictionary<string, Guid> Imports, ImmutableArray<LuaModuleSource> Sources)
{
    internal const int MaximumResources = 32; // Including the handler.
    internal const int MaximumDepth = 16;
    internal const int MaximumSourceCharacters = 65536;
    internal const int MaximumImports = 256;
    internal const int MaximumImportsPerResource = 32;

    internal ImmutableDictionary<Guid, LuaModuleSource> Validate(string rootSource, Action checkpoint)
    {
        if (RootId == Guid.Empty || Sources.IsDefault || Sources.Length >= MaximumResources)
            throw new ArgumentException("Превышен бюджет или отсутствуют данные Lua-модулей.");
        var resources = ImmutableDictionary.CreateBuilder<Guid, LuaModuleSource>();
        resources.Add(RootId, new(RootId, rootSource, Imports));
        foreach (var entry in Sources)
        {
            checkpoint();
            if (entry is null || entry.Id == Guid.Empty || !resources.TryAdd(entry.Id, entry))
                throw new ArgumentException("Повторный или отсутствующий ID Lua-модуля.");
        }
        var characters = 0; var edges = 0;
        foreach (var entry in resources.Values)
        {
            checkpoint();
            if (entry.Source is null || entry.Imports is null || entry.Imports.Count > MaximumImportsPerResource ||
                !LuaActionScript.ValidReferences(entry.Imports) || entry.Imports.Keys.Any(alias =>
                    !System.Text.RegularExpressions.Regex.IsMatch(alias, "\\A[A-Za-z_][A-Za-z0-9_]*\\z")))
                throw new ArgumentException("Некорректный Lua-модуль или его ссылки.");
            if ((characters += entry.Source.Length) > MaximumSourceCharacters || (edges += entry.Imports.Count) > MaximumImports)
                throw new ArgumentException("Превышен общий бюджет кода или связей Lua-модулей.");
            LuaLimits.CheckSource(entry.Source);
            foreach (var id in entry.Imports.Values)
                if (!resources.ContainsKey(id)) throw new ArgumentException($"Отсутствует Lua-модуль {id:N}.");
        }
        var active = new HashSet<Guid>(); var heights = new Dictionary<Guid, int>();
        int Visit(Guid id)
        {
            checkpoint();
            if (heights.TryGetValue(id, out var known)) return known;
            if (!active.Add(id)) throw new ArgumentException($"Циклическая зависимость Lua-модуля {id:N}.");
            var height = 1;
            foreach (var dependency in resources[id].Imports.Values)
                height = Math.Max(height, 1 + Visit(dependency));
            if (height > MaximumDepth) throw new ArgumentException("Превышена глубина зависимостей Lua-модулей.");
            active.Remove(id); heights.Add(id, height); return height;
        }
        Visit(RootId);
        if (heights.Count != resources.Count) throw new ArgumentException("Пакет содержит недостижимый Lua-модуль.");
        return resources.ToImmutable();
    }
}
