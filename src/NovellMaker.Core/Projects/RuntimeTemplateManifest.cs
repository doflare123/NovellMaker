using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovellMaker.Core.Screens.Lua;

namespace NovellMaker.Core.Projects;

/// <summary>Integrity/compatibility contract of a developer-prepared publish. Not a signature or a trust anchor.</summary>
public static class RuntimeTemplateManifest
{
    public const string FileName = "runtime-template.json";
    private const int MaximumFiles = 4096;
    private const string DepsName = "NovellMaker.Runtime.deps.json";
    private const string ConfigName = "NovellMaker.Runtime.runtimeconfig.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    private sealed record FileEntry(string Path, long Length, string Sha256);
    private sealed record Document(int Version, string Rid, int ProjectFormat, int GameManifest, int ScreenWire,
        string[] Features, Guid CoreModule, Guid RuntimeModule, FileEntry[] Files);
    private static readonly string[] Features = ["github_releases_v1", "lua-worker-v1", "screen-lua-worker"];
    private const string Invalid = "Шаблон Windows x64 повреждён или несовместим. Подготовьте его заново скриптом scripts/prepare-windows-export.ps1.";

    /// <summary>Developer step, called by the just-published host before any native initialization.</summary>
    public static void Create(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(root, FileName))) throw new IOException("Манифест шаблона уже существует. Используйте чистый каталог publish.");
        Guard(() =>
        {
            var catalog = Catalog(root);
            Inventory(root, catalog.Keys);
            var core = Inspect(root, "NovellMaker.Core.dll", false);
            var runtime = Inspect(root, "NovellMaker.Runtime.dll", false);
            var files = catalog.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            {
                Inspect(root, p.Key, p.Value);
                using var stream = Open(root, p.Key);
                return new FileEntry(p.Key, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
            }).ToArray();
            var document = new Document(1, "win-x64", NovelProject.CurrentFormatVersion, GamePackage.CurrentVersion,
                ScreenLuaProtocol.Version, Features, core, runtime, files);
            using var output = new FileStream(Path.Combine(root, FileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, document, Json);
        });
    }

    public static ValidatedTemplate Validate(string directory)
    {
        ValidatedTemplate? result = null;
        Guard(() =>
        {
            var root = Path.GetFullPath(directory);
            var document = JsonSerializer.Deserialize<Document>(ReadJson(root, FileName), Json) ?? throw new InvalidDataException();
            if (document.Version != 1 || document.Rid != "win-x64" || document.ProjectFormat != NovelProject.CurrentFormatVersion ||
                document.GameManifest != GamePackage.CurrentVersion || document.ScreenWire != ScreenLuaProtocol.Version ||
                document.Features is null || !document.Features.SequenceEqual(Features) ||
                document.Files is not { Length: > 0 and <= MaximumFiles }) throw new InvalidDataException();
            var files = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.Files)
            {
                if (entry is null || !ValidPath(entry.Path) || entry.Length <= 0 || entry.Sha256 is not { Length: 64 } ||
                    entry.Sha256.Any(c => !char.IsAsciiHexDigit(c)) || !files.TryAdd(entry.Path, entry)) throw new InvalidDataException();
            }
            Inventory(root, files.Keys.Append(FileName));
            foreach (var entry in files.Values)
            {
                using var stream = Open(root, entry.Path);
                if (stream.Length != entry.Length || !Convert.ToHexString(SHA256.HashData(stream)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
            }
            var catalog = Catalog(root);
            if (!catalog.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(files.Keys)) throw new InvalidDataException();
            foreach (var item in catalog) Inspect(root, item.Key, item.Value);
            if (Inspect(root, "NovellMaker.Core.dll", false) != document.CoreModule ||
                Inspect(root, "NovellMaker.Runtime.dll", false) != document.RuntimeModule) throw new InvalidDataException();
            result = new(root, files.Values.Select(e => (e.Path, e.Length, e.Sha256)).ToImmutableArray());
        });
        return result!;
    }

    /// <summary>Immutable admission snapshot. Copy checks the exact bytes again, before output is published.</summary>
    public sealed class ValidatedTemplate
    {
        private readonly string root;
        private readonly ImmutableArray<(string Path, long Length, string Sha256)> files;
        internal ValidatedTemplate(string root, ImmutableArray<(string Path, long Length, string Sha256)> files)
        { this.root = root; this.files = files; }
        public void CopyTo(string destination) => Guard(() =>
        {
            Inventory(root, files.Select(f => f.Path).Append(FileName));
            foreach (var file in files)
            {
                using var input = Open(root, file.Path);
                var target = Path.Combine(destination, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long length = 0; int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    length += count;
                    if (length > file.Length) throw new InvalidDataException();
                    hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
                }
                if (length != file.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
            }
            Inventory(root, files.Select(f => f.Path).Append(FileName));
        });
    }

    private static Dictionary<string, bool> Catalog(string root)
    {
        using var config = JsonDocument.Parse(ReadJson(root, ConfigName));
        var options = config.RootElement.GetProperty("runtimeOptions");
        if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _) ||
            !options.TryGetProperty("includedFrameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array || frameworks.GetArrayLength() == 0)
            throw new InvalidDataException();
        if (!frameworks.EnumerateArray().Any(f => f.GetProperty("name").GetString() == "Microsoft.NETCore.App" &&
                System.Version.TryParse(f.GetProperty("version").GetString(), out var version) && version.Major == 10 && version.Minor == 0))
            throw new InvalidDataException();
        using var deps = JsonDocument.Parse(ReadJson(root, DepsName));
        var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString();
        if (target != ".NETCoreApp,Version=v10.0/win-x64") throw new InvalidDataException();
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        { ["NovellMaker.Runtime.exe"] = true, [DepsName] = false, [ConfigName] = false, ["Licenses/PCG.txt"] = false };
        foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
            foreach (var kind in new[] { "runtime", "native" })
                if (library.Value.TryGetProperty(kind, out var assets))
                    foreach (var asset in assets.EnumerateObject())
                    {
                        var name = asset.Name.Split('/').Last();
                        if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!ValidPath(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException();
                        if (!result.TryAdd(name, kind == "native") && result[name] != (kind == "native")) throw new InvalidDataException();
                    }
        foreach (var required in new[] { "NovellMaker.Runtime.dll", "NovellMaker.Core.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
                     "System.Private.CoreLib.dll", "SDL2.dll", "openal.dll", "libSkiaSharp.dll" })
            if (!result.ContainsKey(required)) throw new InvalidDataException();
        if (result.Count > MaximumFiles) throw new InvalidDataException();
        return result;
    }

    private static Guid Inspect(string root, string path, bool native)
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Guid.Empty;
        using var stream = Open(root, path); using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader is null || pe.PEHeaders.CoffHeader.NumberOfSections == 0 ||
            (native ? pe.HasMetadata || pe.PEHeaders.CoffHeader.Machine != Machine.Amd64 : !pe.HasMetadata)) throw new InvalidDataException();
        if (native) return Guid.Empty;
        var metadata = pe.GetMetadataReader();
        if (path is "NovellMaker.Core.dll" or "NovellMaker.Runtime.dll")
        {
            if (metadata.GetString(metadata.GetAssemblyDefinition().Name) + ".dll" != path) throw new InvalidDataException();
            if (path == "NovellMaker.Core.dll")
            {
                CheckConstant(metadata, "NovellMaker.Core.Projects", "NovelProject", "CurrentFormatVersion", NovelProject.CurrentFormatVersion);
                CheckConstant(metadata, "NovellMaker.Core.Projects", "GamePackage", "CurrentVersion", GamePackage.CurrentVersion);
                CheckConstant(metadata, "NovellMaker.Core.Screens.Lua", "ScreenLuaProtocol", "Version", ScreenLuaProtocol.Version);
            }
        }
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    private static void CheckConstant(MetadataReader metadata, string ns, string type, string field, int expected)
    {
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            if (metadata.GetString(definition.Namespace) != ns || metadata.GetString(definition.Name) != type) continue;
            foreach (var id in definition.GetFields())
            {
                var value = metadata.GetFieldDefinition(id);
                if (metadata.GetString(value.Name) != field || value.GetDefaultValue().IsNil) continue;
                var constant = metadata.GetConstant(value.GetDefaultValue());
                if (constant.TypeCode == ConstantTypeCode.Int32 && metadata.GetBlobReader(constant.Value).ReadInt32() == expected) return;
            }
        }
        throw new InvalidDataException();
    }

    private static void Inventory(string root, IEnumerable<string> expected)
    {
        var remaining = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directories = new Stack<string>(); directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/') + "/";
                    if (!remaining.Any(f => f.StartsWith(relative, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException();
                    directories.Push(path);
                }
                else if (!remaining.Remove(Path.GetRelativePath(root, path).Replace('\\', '/'))) throw new InvalidDataException();
            }
        }
        if (remaining.Count != 0) throw new InvalidDataException();
    }

    private static bool ValidPath(string? value) => value is { Length: > 0 and <= 240 } &&
        value.Split('/').All(p => p is not ("" or "." or "..") && !p.EndsWith('.') &&
            p.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'));

    private static FileStream Open(string root, string path)
    {
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        foreach (var part in path.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        }
        return new(current, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static byte[] ReadJson(string root, string path)
    {
        using var stream = Open(root, path);
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException();
        using var buffer = new MemoryStream(); var chunk = new byte[65536]; int count;
        while ((count = stream.Read(chunk)) != 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        return buffer.ToArray();
    }

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or BadImageFormatException)
        { throw new IOException(Invalid); } // No arbitrary filenames, JSON paths or payload bytes in diagnostics.
    }
}
