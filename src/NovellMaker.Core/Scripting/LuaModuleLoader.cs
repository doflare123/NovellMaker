using System.Collections.Immutable;
using MoonSharp.Interpreter;

namespace NovellMaker.Core.Scripting;

/// <summary>VM-local cache and environments. Imports call Lua chunks in the caller's coroutine, never recursively through CLR.</summary>
internal sealed class LuaModuleLoader(Script script, LuaStandardLibrary library)
{
    private readonly Dictionary<Guid, DynValue> chunks = [];
    private readonly Dictionary<Guid, DynValue> exports = [];
    private readonly HashSet<Guid> loading = [];

    internal DynValue Compile(string source, LuaModuleBundle? bundle, Table ui)
    {
        var resources = bundle?.Validate(source, () => { }) ?? ImmutableDictionary<Guid, LuaModuleSource>.Empty.Add(Guid.Empty,
            new(Guid.Empty, source, ImmutableDictionary<string, Guid>.Empty));
        var apis = new Table(script);
        foreach (var entry in resources.Values)
        {
            var root = entry.Id == (bundle?.RootId ?? Guid.Empty);
            var environment = root ? script.Globals : new Table(script);
            library.Install(environment);
            var api = new Table(script); foreach (var pair in ui.Pairs) api.Set(pair.Key, pair.Value);
            var aliases = new Table(script);
            foreach (var pair in entry.Imports) aliases.Set(pair.Key, DynValue.NewString(pair.Value.ToString("N")));
            environment.Set("ui", DynValue.NewTable(api)); environment.Set("modules", DynValue.NewTable(aliases));
            if (!root) environment.Set("refs", DynValue.NewTable(new Table(script)));
            var allowed = entry.Imports.Values.ToHashSet();
            var descriptor = new Table(script);
            descriptor.Set("ui", DynValue.NewTable(api));
            descriptor.Set("resolve", DynValue.NewCallback((_, args) =>
            {
                if (args[0].Type != DataType.String || !Guid.TryParse(args[0].String, out var id) || !allowed.Contains(id))
                    throw new ScriptRuntimeException("Lua-модуль не объявлен: используйте ui.import(modules.<имя>) и ссылки Lua-ресурса.");
                if (exports.TryGetValue(id, out var value)) return DynValue.NewTuple(value, DynValue.Nil);
                if (!loading.Add(id)) throw new ScriptRuntimeException($"Циклический импорт Lua-модуля {id:N}.");
                return DynValue.NewTuple(DynValue.Nil, chunks[id]);
            }));
            apis.Set(apis.Length + 1, DynValue.NewTable(descriptor));
            chunks.Add(entry.Id, script.LoadString(entry.Source, environment, bundle is null ? "screen.lua" : $"Scripts/{entry.Id:N}.lua"));
        }
        var bootstrap = new Table(script);
        bootstrap.Set("apis", DynValue.NewTable(apis));
        bootstrap.Set("root", chunks[bundle?.RootId ?? Guid.Empty]);
        bootstrap.Set("publish", DynValue.NewCallback((_, args) =>
        {
            var id = Guid.Parse(args[0].String);
            if (args[1].Type != DataType.Table || args[1].Table.MetaTable is not null)
                throw new ScriptRuntimeException($"Lua-модуль {id:N} должен вернуть таблицу экспортов.");
            exports.Add(id, args[1]); loading.Remove(id); return args[1];
        }));
        // Trusted bootstrap installs closures without exposing its tables/callbacks to author environments.
        return script.LoadString("""
            for i=1,#apis do
              local api=apis[i]
              api.ui.import=function(id)
                local value,chunk=api.resolve(id)
                if chunk==nil then return value end
                value=chunk()
                return publish(id,value)
              end
            end
            return root()
            """, bootstrap, "module-loader.lua");
    }
}
