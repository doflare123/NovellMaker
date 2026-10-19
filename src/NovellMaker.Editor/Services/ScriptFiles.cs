using System.Text;
using NovellMaker.Core.Execution;

namespace NovellMaker.Editor.Services;

/// <summary>Stable source file locations; runtime continues to consume the validated project snapshot.</summary>
internal static class ScriptFiles
{
    public static string Resolve(string root, Guid id)
    {
        var folder = Path.Combine(Path.GetFullPath(root), "Scripts");
        var file = Path.Combine(folder, id.ToString("N") + ".lua");
        foreach (var path in new[] { folder, file })
        {
            var info = (FileSystemInfo)(path == folder ? new DirectoryInfo(path) : new FileInfo(path));
            if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Путь Lua проходит через ссылку: " + path);
        }
        return file;
    }

    public static string Read(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > LuaActionScript.MaximumSourceLength * 4L + 3) throw new IOException("Превышен размер Lua-скрипта.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        var buffer = new char[LuaActionScript.MaximumSourceLength + 2];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        var offset = count > 0 && buffer[0] == '\uFEFF' ? 1 : 0;
        if (count - offset > LuaActionScript.MaximumSourceLength) throw new IOException("Превышен размер Lua-скрипта.");
        return new string(buffer, offset, count - offset);
    }

    public static string Ensure(string root, LuaScriptAsset script, bool requireSame = false)
    {
        var file = Resolve(root, script.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            if (requireSame && Read(file) != script.Source) throw new IOException("Файл Lua в целевой папке отличается: " + file);
            return file;
        }
        Write(file, script.Source, overwrite: false);
        return file;
    }

    public static void Write(string file, string source, bool overwrite)
    {
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, source, new UTF8Encoding(false)); File.Move(temporary, file, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
