using System.Reflection;

namespace NovellMaker.Editor.Services;

internal static class LuaAuthoringTools
{
    internal static string JournalPath(Guid projectId) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NovellMaker", "Saves", projectId.ToString(), "lua-diagnostics.log");

    internal static string Install(string root)
    {
        var scripts = Path.Combine(Path.GetFullPath(root), "Scripts");
        var directory = Path.Combine(scripts, "NovellMakerApi");
        var assembly = typeof(LuaAuthoringTools).Assembly;
        var prefix = "NovellMaker.Editor.LuaApi.";
        var sources = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order().ToArray();
        // Preflight all destinations before replacing engine-owned metadata; never follow source/project links.
        foreach (var path in sources.Select(n => Path.Combine(directory, n[prefix.Length..])).Prepend(directory).Prepend(scripts))
        {
            var info = (FileSystemInfo)(Path.HasExtension(path) ? new FileInfo(path) : new DirectoryInfo(path));
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Путь аннотаций Lua проходит через ссылку: " + path);
        }
        Directory.CreateDirectory(directory);
        foreach (var resource in sources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            ScriptFiles.Write(Path.Combine(directory, resource[prefix.Length..]), reader.ReadToEnd(), overwrite: true);
        }
        return Path.Combine(directory, "README.md");
    }
}
