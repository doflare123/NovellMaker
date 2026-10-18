using System.Security.Cryptography;

namespace NovellMaker.Editor.Services;

/// <summary>A reversible on-disk deletion. Backups are outside the author's project.</summary>
internal sealed record ProjectFileDeletion(string ProjectRoot, string OriginalPath, string BackupPath, bool IsDirectory)
{
    public static string Validate(string root, string path, string projectFile)
    {
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new IOException("Удалять можно только файлы и папки внутри текущего проекта.");
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new IOException("Служебные файлы Git удалять нельзя.");
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Удаление через символическую ссылку не поддерживается.");
        }
        if (Same(path, projectFile) || Directory.Exists(path) && IsWithin(path, projectFile))
            throw new IOException("Нельзя удалить открытый файл проекта или содержащую его папку.");
        if (Directory.Exists(path)) CheckTree(path);
        return path;
    }
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    internal static bool IsWithin(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && !Path.IsPathFullyQualified(relative);
    }
    private static void CheckTree(string directory)
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (item.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка содержит служебные файлы Git или ссылки. Удаление отменено.");
            if (item is DirectoryInfo child) CheckTree(child.FullName);
        }
    }
    public static ProjectFileDeletion Create(string root, string path, string projectFile)
    {
        path = Validate(root, path, projectFile);
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path)) throw new FileNotFoundException("Файл уже отсутствует.", path);
        var backupDirectory = Path.Combine(Path.GetTempPath(), "NovellMaker", "DeletedFiles", Guid.NewGuid().ToString("N"));
        if (IsWithin(path, backupDirectory)) throw new IOException("Резервная копия не может находиться внутри удаляемой папки.");
        Directory.CreateDirectory(backupDirectory);
        var deletion = new ProjectFileDeletion(Path.GetFullPath(root), path, Path.Combine(backupDirectory, "content"), isDirectory);
        if (isDirectory) CopyTree(path, deletion.BackupPath);
        else File.Copy(path, deletion.BackupPath);
        try { deletion.DeleteOriginal(projectFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Удаление не завершено. Резервная копия сохранена: {deletion.BackupPath}", error);
        }
        return deletion;
    }
    public void Restore(string projectFile)
    {
        Validate(ProjectRoot, OriginalPath, projectFile);
        if (File.Exists(OriginalPath) || Directory.Exists(OriginalPath))
            throw new IOException("Нельзя восстановить удалённый объект: по исходному пути уже существует файл или папка. Существующие данные не перезаписаны.");
        Directory.CreateDirectory(Path.GetDirectoryName(OriginalPath)!);
        if (IsDirectory) CopyTree(BackupPath, OriginalPath);
        else File.Copy(BackupPath, OriginalPath, overwrite: false);
    }
    public void DeleteOriginal(string projectFile)
    {
        Validate(ProjectRoot, OriginalPath, projectFile);
        if (!ContentsMatch()) throw new IOException("Файлы изменились после создания резервной копии. Удаление отменено.");
        if (IsDirectory) Directory.Delete(OriginalPath, recursive: true);
        else File.Delete(OriginalPath);
    }
    private bool ContentsMatch()
    {
        if (!IsDirectory) return EqualFile(OriginalPath, BackupPath);
        var originalFiles = Directory.GetFiles(OriginalPath, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(OriginalPath, p)).Order().ToArray();
        var backupFiles = Directory.GetFiles(BackupPath, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(BackupPath, p)).Order().ToArray();
        var originalDirectories = Directory.GetDirectories(OriginalPath, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(OriginalPath, p)).Order().ToArray();
        var backupDirectories = Directory.GetDirectories(BackupPath, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(BackupPath, p)).Order().ToArray();
        return originalFiles.SequenceEqual(backupFiles) && originalDirectories.SequenceEqual(backupDirectories) &&
            originalFiles.All(p => EqualFile(Path.Combine(OriginalPath, p), Path.Combine(BackupPath, p)));
    }
    private static bool EqualFile(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        return a.Length == b.Length && SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
    private static void CopyTree(string source, string target)
    {
        CheckTree(source);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var directory in Directory.GetDirectories(source)) CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
    }
}
