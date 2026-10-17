namespace NovellMaker.Core.Assets;

/// <summary>
/// Validates portable project asset paths and resolves them without following
/// an existing reparse point below the selected project directory.
/// </summary>
public static class AssetPath
{
    private const string ImageDirectoryPrefix = "Assets/Images/";

    private static readonly HashSet<string> ReservedWindowsNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "COM¹", "COM²", "COM³",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
            "LPT¹", "LPT²", "LPT³",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static void ValidateRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Contains('\\') ||
            Path.IsPathFullyQualified(path) ||
            path.StartsWith('/') ||
            !path.StartsWith(ImageDirectoryPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Путь изображения должен быть относительным путём внутри Assets/Images/ с разделителем '/'.",
                nameof(path));
        }

        var segments = path.Split('/');
        if (segments.Any(static segment => segment.Length == 0))
        {
            throw new ArgumentException("Путь изображения содержит пустой сегмент.", nameof(path));
        }

        foreach (var segment in segments)
        {
            ValidateSegment(segment, path);
        }

        var extension = Path.GetExtension(segments[^1]);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Поддерживаются только изображения PNG и JPEG.", nameof(path));
        }
    }

    public static string Resolve(string projectDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ValidateRelativePath(relativePath);

        var projectRoot = Path.GetFullPath(projectDirectory);
        if (!Directory.Exists(projectRoot))
        {
            throw new DirectoryNotFoundException($"Каталог проекта не найден: {projectRoot}");
        }

        var candidate = Path.GetFullPath(
            Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var pathFromRoot = Path.GetRelativePath(projectRoot, candidate);
        if (Path.IsPathFullyQualified(pathFromRoot) ||
            pathFromRoot.Equals("..", StringComparison.Ordinal) ||
            pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("Путь изображения выходит за пределы каталога проекта.", nameof(relativePath));
        }

        var current = projectRoot;
        var segments = relativePath.Split('/');
        for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            current = Path.Combine(current, segment);

            var fileSystemInfo = segmentIndex < segments.Length - 1
                ? (FileSystemInfo)new DirectoryInfo(current)
                : new FileInfo(current);
            fileSystemInfo.Refresh();
            if (fileSystemInfo.LinkTarget is not null)
            {
                throw new IOException(
                    $"Путь ресурса проходит через символическую ссылку: {current}");
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Путь ресурса проходит через ссылку или точку повторного анализа: {current}");
            }

            if (segmentIndex < segments.Length - 1 && (attributes & FileAttributes.Directory) == 0)
            {
                throw new IOException($"Компонент пути ресурса не является каталогом: {current}");
            }
        }

        return candidate;
    }

    private static void ValidateSegment(string segment, string path)
    {
        if (segment is "." or ".." ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.Any(static character => character < 32 || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
        {
            throw new ArgumentException($"Путь изображения содержит недопустимый сегмент: {path}", nameof(path));
        }

        var deviceName = segment.Split('.')[0].TrimEnd(' ', '.');
        if (ReservedWindowsNames.Contains(deviceName))
        {
            throw new ArgumentException($"Путь изображения содержит зарезервированное имя: {segment}", nameof(path));
        }
    }
}
