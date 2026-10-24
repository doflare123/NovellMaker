using System.Text.Json;

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
        catch (JsonException exception)
        {
            throw new ProjectFormatException(
                "Файл проекта содержит некорректный JSON или не соответствует ожидаемой структуре.",
                exception);
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

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

}
