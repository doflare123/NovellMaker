using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NovellMaker.Editor.Services.Git;

public sealed class GitStatusService : IGitStatusService
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(2);

    public async Task<GitStatusSnapshot> GetStatusAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string projectRoot;
        try
        {
            projectRoot = Path.GetFullPath(projectDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error($"Некорректный путь проекта: {exception.Message}");
        }

        if (!Directory.Exists(projectRoot))
        {
            return Error("Каталог проекта не существует.");
        }

        ProcessResult repositoryResult;
        try
        {
            repositoryResult = await RunGitAsync(
                projectRoot,
                ["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--show-prefix"],
                cancellationToken);
        }
        catch (Win32Exception exception) when (IsExecutableMissing(exception))
        {
            return new(
                GitAvailability.NotInstalled,
                null,
                [],
                "Git не установлен или недоступен через PATH.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Error($"Не удалось определить репозиторий: {exception.Message}");
        }

        if (repositoryResult.ExitCode != 0)
        {
            if (IsNotRepository(repositoryResult.StandardError))
            {
                return new(
                    GitAvailability.NotRepository,
                    null,
                    [],
                    "Каталог проекта не находится в рабочем дереве Git.");
            }

            return Error(BuildGitError("Не удалось определить репозиторий", repositoryResult));
        }

        var repositoryOutput = repositoryResult.StandardOutput
            .Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

        if (repositoryOutput.Length < 3 || !string.Equals(repositoryOutput[0], "true", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                GitAvailability.NotRepository,
                null,
                [],
                "Каталог проекта не находится в рабочем дереве Git.");
        }

        string repositoryRoot;
        try
        {
            repositoryRoot = Path.GetFullPath(repositoryOutput[1]);
            // Git resolves ancestor symlinks (e.g. /var -> /private/var on macOS).
            // Use its project prefix so both paths used for filtering share that spelling.
            projectRoot = repositoryOutput[2].Length == 0
                ? repositoryRoot
                : Path.GetFullPath(repositoryOutput[2], repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error($"Git вернул некорректный путь репозитория: {exception.Message}");
        }

        ProcessResult statusResult;
        try
        {
            statusResult = await RunGitAsync(
                projectRoot,
                ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--", "."],
                cancellationToken);
        }
        catch (Win32Exception exception) when (IsExecutableMissing(exception))
        {
            return new(
                GitAvailability.NotInstalled,
                null,
                [],
                "Git не установлен или недоступен через PATH.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Error($"Не удалось прочитать состояние Git: {exception.Message}", repositoryRoot);
        }

        if (statusResult.ExitCode != 0)
        {
            return Error(BuildGitError("Не удалось прочитать состояние Git", statusResult), repositoryRoot);
        }

        try
        {
            var changes = ParseChanges(statusResult.StandardOutput, repositoryRoot, projectRoot);
            var message = changes.Count == 0
                ? "Нет незакоммиченных изменений."
                : $"Незакоммиченных изменений: {changes.Count}.";

            return new(GitAvailability.Available, repositoryRoot, changes, message);
        }
        catch (FormatException exception)
        {
            return Error($"Не удалось разобрать ответ Git: {exception.Message}", repositoryRoot);
        }
    }

    private static async Task<ProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        process.StartInfo.Environment["LC_ALL"] = "C";
        process.StartInfo.Environment["LANG"] = "C";
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardErrorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeoutSource = new CancellationTokenSource(ProcessTimeout);
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(waitSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            TryClose(process.StandardOutput);
            TryClose(process.StandardError);

            try
            {
                await Task.WhenAll(standardOutputTask, standardErrorTask)
                    .WaitAsync(StreamDrainTimeout, CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException or ObjectDisposedException or TimeoutException)
            {
                // The result is already being discarded. Bound cleanup so a child process
                // that inherited a redirected pipe cannot hold cancellation indefinitely.
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Git не завершил работу за {ProcessTimeout.TotalSeconds:0} секунд.");
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        return new(process.ExitCode, standardOutput, standardError);
    }

    private static IReadOnlyList<GitFileChange> ParseChanges(
        string porcelainOutput,
        string repositoryRoot,
        string projectRoot)
    {
        var records = porcelainOutput.Split('\0');
        var changes = new List<GitFileChange>();

        for (var index = 0; index < records.Length - 1; index++)
        {
            var record = records[index];
            if (record.Length < 4 || record[2] != ' ')
            {
                throw new FormatException("Получена запись неизвестного формата.");
            }

            var indexStatus = record[0].ToString();
            var workTreeStatus = record[1].ToString();
            var repositoryPath = record[3..];
            string? originalRepositoryPath = null;

            if (indexStatus is "R" or "C" || workTreeStatus is "R" or "C")
            {
                index++;
                if (index >= records.Length - 1)
                {
                    throw new FormatException("В записи переименования отсутствует исходный путь.");
                }

                originalRepositoryPath = records[index];
            }

            var projectPath = ToProjectRelativePath(repositoryPath, repositoryRoot, projectRoot);
            if (projectPath is null)
            {
                continue;
            }

            var originalProjectPath = originalRepositoryPath is null
                ? null
                : ToProjectRelativePath(originalRepositoryPath, repositoryRoot, projectRoot);

            changes.Add(new GitFileChange(
                projectPath,
                indexStatus,
                workTreeStatus,
                originalProjectPath));
        }

        return changes;
    }

    private static string? ToProjectRelativePath(
        string repositoryPath,
        string repositoryRoot,
        string projectRoot)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(repositoryPath.Replace('/', Path.DirectorySeparatorChar), repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(projectRoot, fullPath);
        if (relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison))
        {
            return null;
        }

        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsNotRepository(string standardError) =>
        standardError.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ||
        standardError.Contains("not inside a work tree", StringComparison.OrdinalIgnoreCase) ||
        standardError.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutableMissing(Win32Exception exception) =>
        exception.NativeErrorCode is 2 or 3;

    private static string BuildGitError(string prefix, ProcessResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"код завершения {result.ExitCode}"
            : result.StandardError.Trim();
        return $"{prefix}: {details}";
    }

    private static GitStatusSnapshot Error(string message, string? repositoryRoot = null) =>
        new(GitAvailability.Error, repositoryRoot, [], message);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
        }
        catch (Win32Exception)
        {
            // Waiting stream readers still prevents a fire-and-forget process handle.
        }
    }

    private static void TryClose(TextReader reader)
    {
        try
        {
            reader.Close();
        }
        catch (IOException)
        {
            // The process may have closed the pipe concurrently.
        }
        catch (ObjectDisposedException)
        {
            // The pipe was already closed during process termination.
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
