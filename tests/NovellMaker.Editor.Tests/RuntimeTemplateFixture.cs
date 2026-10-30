using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using NovellMaker.Core.Projects;

namespace NovellMaker.Editor.Tests;

/// <summary>Structural PE fixtures for packaging tests; the native smoke uses a real self-contained publish.</summary>
internal static class RuntimeTemplateFixture
{
    internal const string Config = """{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.8"}]}}""";

    internal static void Create(string root)
    {
        Directory.CreateDirectory(root);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "NovellMaker.slnx"))) repo = repo.Parent;
        if (repo is null) throw new InvalidOperationException("Repository unavailable.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        File.Copy(typeof(NovelProject).Assembly.Location, Path.Combine(root, "NovellMaker.Core.dll"));
        File.Copy(Path.Combine(repo.FullName, "src", "NovellMaker.Runtime", "bin", configuration, "net10.0", "NovellMaker.Runtime.dll"), Path.Combine(root, "NovellMaker.Runtime.dll"));
        var native = new JsonObject();
        foreach (var file in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "SDL2.dll", "openal.dll", "libSkiaSharp.dll" })
        { WriteNative(Path.Combine(root, file)); native[file] = new JsonObject(); }
        WriteNative(Path.Combine(root, "NovellMaker.Runtime.exe"));
        File.Copy(typeof(NovelProject).Assembly.Location, Path.Combine(root, "System.Private.CoreLib.dll"));
        var deps = new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = ".NETCoreApp,Version=v10.0/win-x64" },
            ["targets"] = new JsonObject { [".NETCoreApp,Version=v10.0/win-x64"] = new JsonObject
            {
                ["fixture/1.0"] = new JsonObject { ["native"] = native, ["runtime"] = new JsonObject
                {
                    ["NovellMaker.Runtime.dll"] = new JsonObject(), ["NovellMaker.Core.dll"] = new JsonObject(),
                    ["System.Private.CoreLib.dll"] = new JsonObject()
                } }
            } }
        };
        File.WriteAllText(Path.Combine(root, "NovellMaker.Runtime.deps.json"), deps.ToJsonString());
        File.WriteAllText(Path.Combine(root, "NovellMaker.Runtime.runtimeconfig.json"), Config);
        Directory.CreateDirectory(Path.Combine(root, "Licenses")); File.WriteAllText(Path.Combine(root, "Licenses", "PCG.txt"), "license fixture");
        RuntimeTemplateManifest.Create(root);
    }

    internal static void Reseal(string root)
    { File.Delete(Path.Combine(root, RuntimeTemplateManifest.FileName)); RuntimeTemplateManifest.Create(root); }

    private static void WriteNative(string path)
    {
        using var buffer = new MemoryStream(new byte[1024]); using var writer = new BinaryWriter(buffer);
        writer.Write((ushort)0x5a4d); buffer.Position = 0x3c; writer.Write(128);
        buffer.Position = 128; writer.Write(0x4550); writer.Write((ushort)0x8664); writer.Write((ushort)1);
        buffer.Position = 148; writer.Write((ushort)240); writer.Write((ushort)0x2022);
        writer.Write((ushort)0x20b); buffer.Position = 184; writer.Write(4096); writer.Write(512);
        buffer.Position = 208; writer.Write(8192); writer.Write(512);
        buffer.Position = 220; writer.Write((ushort)3); buffer.Position = 260; writer.Write(16);
        buffer.Position = 392; writer.Write(new byte[] { 46, 116, 101, 120, 116, 0, 0, 0 });
        writer.Write(1); writer.Write(4096); writer.Write(512); writer.Write(512);
        buffer.Position = 512; writer.Write((byte)0xc3);
        File.WriteAllBytes(path, buffer.ToArray());
    }

    internal static void WriteOldCore(string path)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("NovellMaker.Core.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("NovellMaker.Core"), new Version(1, 0), default, default, 0, System.Reflection.AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(System.Reflection.TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var field = metadata.AddFieldDefinition(System.Reflection.FieldAttributes.Public | System.Reflection.FieldAttributes.Static | System.Reflection.FieldAttributes.Literal,
            metadata.GetOrAddString("CurrentFormatVersion"), metadata.GetOrAddBlob(new byte[] { 6, 8 }));
        metadata.AddConstant(field, 19);
        metadata.AddTypeDefinition(System.Reflection.TypeAttributes.Public, metadata.GetOrAddString("NovellMaker.Core.Projects"), metadata.GetOrAddString("NovelProject"),
            default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        File.WriteAllBytes(path, image.ToArray());
    }
}
