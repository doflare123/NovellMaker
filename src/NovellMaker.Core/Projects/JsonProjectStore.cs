using System.Text.Json;
using System.Security.Cryptography;

namespace NovellMaker.Core.Projects;

public sealed class JsonProjectStore : IProjectStore
{
    public async Task<NovelProject> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        NovelProject project;

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            project = await ProjectJsonCodec.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectFormatException)
        {
            throw;
        }
        catch (JsonException)
        {
            // JSON paths/property names can contain untrusted credential text. Do not retain raw parser diagnostics in logs.
            throw new ProjectFormatException(
                "Файл проекта содержит некорректный JSON или не соответствует ожидаемой структуре.");
        }
        catch (NotSupportedException exception)
        {
            throw new ProjectFormatException(
                "Файл проекта использует неподдерживаемую структуру данных.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException(
                $"Файл проекта содержит некорректные данные: {exception.Message}",
                exception);
        }

        try
        {
            NovelProject.ValidateForPersistence(project);
        }
        catch (ArgumentException exception)
        {
            throw new ProjectFormatException(
                $"Файл проекта содержит некорректные данные: {exception.Message}",
                exception);
        }

        return project;
    }

    public async Task SaveAsync(
        string path,
        NovelProject project,
        CancellationToken cancellationToken = default)
        => await SaveIfUnchangedAsync(path, project, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Checks the original byte revision immediately before replacement and returns the written revision.</summary>
    public async Task<string> SaveIfUnchangedAsync(string path, NovelProject project, string? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(project);
        NovelProject.ValidateForPersistence(project);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Не удалось определить каталог файла проекта.", nameof(path));
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await ProjectJsonCodec.WriteAsync(stream, project, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            string revision;
            await using (var written = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                revision = Convert.ToHexString(await SHA256.HashDataAsync(written, cancellationToken).ConfigureAwait(false));
            await using var ownership = await AcquireSaveLockAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (expectedRevision is not null)
            {
                await using var original = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(original, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(expectedRevision, actual, StringComparison.Ordinal))
                    throw new IOException("Файл проекта изменён вне редактора. Используйте «Сохранить как…» для сохранения своей версии отдельно.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            return revision;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<FileStream> AcquireSaveLockAsync(string path, CancellationToken cancellationToken)
    {
        // Cooperating editor instances must not both pass the revision check before either renames its file.
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var directory = Path.Combine(Path.GetTempPath(), "NovellMaker", "ProjectSaveLocks");
        Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".lock");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(2))
            { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
        }
    }

}
