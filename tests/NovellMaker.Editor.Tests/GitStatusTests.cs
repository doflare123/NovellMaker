using System.Diagnostics;
using System.Text;
using NovellMaker.Editor.Services.Git;

namespace NovellMaker.Editor.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GitStatusTests
{
    [TestMethod]
    public async Task GetStatusAsync_ReturnsError_ForMissingProjectDirectory()
    {
        var missingDirectory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NovellMaker.GitTests",
            Guid.NewGuid().ToString("N"));

        var snapshot = await new GitStatusService().GetStatusAsync(missingDirectory);

        Assert.AreEqual(GitAvailability.Error, snapshot.Availability);
        Assert.AreEqual(0, snapshot.Changes.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Message));
    }

    [TestMethod]
    public async Task GetStatusAsync_PropagatesCancellation()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => new GitStatusService().GetStatusAsync(directory.Path, cancellation.Token));
    }

    [TestMethod]
    public async Task GetStatusAsync_ReturnsNotRepository_ForOrdinaryDirectory()
    {
        using var directory = new TemporaryDirectory();
        var ceilingVariable = Environment.GetEnvironmentVariable("GIT_CEILING_DIRECTORIES");
        GitStatusSnapshot snapshot;
        try
        {
            Environment.SetEnvironmentVariable(
                "GIT_CEILING_DIRECTORIES",
                System.IO.Path.GetDirectoryName(directory.Path));
            snapshot = await new GitStatusService().GetStatusAsync(directory.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", ceilingVariable);
        }

        Assert.AreEqual(GitAvailability.NotRepository, snapshot.Availability);
        Assert.IsNull(snapshot.RepositoryRoot);
        Assert.AreEqual(0, snapshot.Changes.Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(snapshot.Message));
    }

    [TestMethod]
    public async Task GetStatusAsync_ReportsStagedUnstagedDeletedRenamedAndUnicodePaths()
    {
        using var repository = GitRepository.Create();
        var projectDirectory = repository.CreateDirectory("Novel Project");
        repository.Write("Novel Project/staged.txt", "before");
        repository.Write("Novel Project/unstaged.txt", "before");
        repository.Write("Novel Project/deleted.txt", "before");
        repository.Write("Novel Project/original name.txt", "before");
        repository.CommitAll("seed project");

        repository.Write("Novel Project/staged.txt", "staged change");
        repository.Git("add", "--", "Novel Project/staged.txt");
        repository.Write("Novel Project/unstaged.txt", "work tree change");
        File.Delete(System.IO.Path.Combine(projectDirectory, "deleted.txt"));
        repository.Git("mv", "--", "Novel Project/original name.txt", "Novel Project/renamed name.txt");
        repository.Write("Novel Project/сцена 01.txt", "новая сцена");

        var snapshot = await new GitStatusService().GetStatusAsync(projectDirectory);

        Assert.AreEqual(GitAvailability.Available, snapshot.Availability);
        Assert.AreEqual(System.IO.Path.GetFullPath(repository.Root), snapshot.RepositoryRoot);
        Assert.AreEqual(5, snapshot.Changes.Count);
        AssertChange(snapshot, "staged.txt", "M", " ");
        AssertChange(snapshot, "unstaged.txt", " ", "M");
        AssertChange(snapshot, "deleted.txt", " ", "D");
        var renamed = AssertChange(snapshot, "renamed name.txt", "R", " ");
        Assert.AreEqual("original name.txt", renamed.OriginalPath);
        AssertChange(snapshot, "сцена 01.txt", "?", "?");
    }

    [TestMethod]
    public async Task GetStatusAsync_UsesGitIgnoreAndOnlyReturnsChangesInsideProjectDirectory()
    {
        using var repository = GitRepository.Create();
        var projectDirectory = repository.CreateDirectory("Novel Project");
        repository.CreateDirectory("Sibling");
        repository.Write(
            "Novel Project/.gitignore",
            "*.tmp\ngenerated/**\n!generated/\n!generated/keep/\n!generated/keep/visible.tmp\n");
        repository.Write("Novel Project/tracked.tmp", "tracked despite ignore");
        repository.Git("add", "-f", "--", "Novel Project/tracked.tmp");
        repository.Git("add", "--", "Novel Project/.gitignore");
        repository.Git("commit", "-m", "seed ignore rules");

        repository.Write("Novel Project/tracked.tmp", "tracked modification");
        repository.Write("Novel Project/ignored.tmp", "ignored");
        repository.Write("Novel Project/generated/dropped.txt", "ignored nested file");
        repository.Write("Novel Project/generated/keep/visible.tmp", "explicitly included");
        repository.Write("Sibling/outside.txt", "outside opened project");

        var snapshot = await new GitStatusService().GetStatusAsync(projectDirectory);

        Assert.AreEqual(GitAvailability.Available, snapshot.Availability);
        CollectionAssert.AreEquivalent(
            new[] { "tracked.tmp", "generated/keep/visible.tmp" },
            snapshot.Changes.Select(change => change.Path).ToArray());
        AssertChange(snapshot, "tracked.tmp", " ", "M");
        AssertChange(snapshot, "generated/keep/visible.tmp", "?", "?");
        Assert.IsFalse(snapshot.Changes.Any(change => change.Path.Contains("Sibling", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task GetStatusAsync_ReportsMergeConflict()
    {
        using var repository = GitRepository.Create();
        var projectDirectory = repository.CreateDirectory("Novel");
        repository.Write("Novel/conflict.txt", "base\n");
        repository.CommitAll("base");

        repository.Git("switch", "-c", "other");
        repository.Write("Novel/conflict.txt", "other branch\n");
        repository.CommitAll("other change");
        repository.Git("switch", "main");
        repository.Write("Novel/conflict.txt", "main branch\n");
        repository.CommitAll("main change");
        var merge = repository.GitAllowFailure("merge", "other");
        Assert.AreNotEqual(0, merge.ExitCode, "The test setup must create a real merge conflict.");

        var snapshot = await new GitStatusService().GetStatusAsync(projectDirectory);

        Assert.AreEqual(GitAvailability.Available, snapshot.Availability);
        var conflict = AssertChange(snapshot, "conflict.txt", "U", "U");
        Assert.AreEqual("Конфликт", conflict.DisplayStatus);
    }

    [TestMethod]
    public async Task GetStatusAsync_DoesNotExecuteConfiguredFileSystemMonitorHook()
    {
        using var repository = GitRepository.Create();
        var projectDirectory = repository.CreateDirectory("Novel");
        repository.Write("Novel/project.json", "{}");
        repository.CommitAll("seed project");

        var markerPath = System.IO.Path.Combine(repository.Root, "fsmonitor-invoked.marker");
        var hookPath = repository.WriteExecutableShellScript(
            "fsmonitor-hook",
            $"printf invoked > '{EscapeForSingleQuotedShell(ToGitPath(markerPath))}'\nprintf 'token\\0'\n");
        repository.Git("config", "core.fsmonitor", ToGitPath(hookPath));

        repository.Git("status", "--porcelain=v1");
        Assert.IsTrue(File.Exists(markerPath), "The control git status must execute the configured fsmonitor hook.");
        File.Delete(markerPath);

        var snapshot = await new GitStatusService().GetStatusAsync(projectDirectory);

        Assert.AreEqual(GitAvailability.Available, snapshot.Availability);
        Assert.IsFalse(File.Exists(markerPath), "Reading editor status must disable repository-configured fsmonitor hooks.");
    }

    private static GitFileChange AssertChange(
        GitStatusSnapshot snapshot,
        string path,
        string indexStatus,
        string workTreeStatus)
    {
        var change = snapshot.Changes.SingleOrDefault(candidate => candidate.Path == path);
        Assert.IsNotNull(change, $"Git status did not contain '{path}'. Actual: {string.Join(", ", snapshot.Changes.Select(item => $"{item.IndexStatus}{item.WorkTreeStatus} {item.Path}"))}");
        Assert.AreEqual(indexStatus, change.IndexStatus, $"Unexpected index status for '{path}'.");
        Assert.AreEqual(workTreeStatus, change.WorkTreeStatus, $"Unexpected work tree status for '{path}'.");
        return change;
    }

    private static string EscapeForSingleQuotedShell(string value) =>
        value.Replace("'", "'\"'\"'", StringComparison.Ordinal);

    private static string ToGitPath(string path) =>
        path.Replace(System.IO.Path.DirectorySeparatorChar, '/');

    private sealed class GitRepository : IDisposable
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
        private readonly TemporaryDirectory _directory;

        private GitRepository(TemporaryDirectory directory)
        {
            _directory = directory;
            Root = directory.Path;
        }

        public string Root { get; }

        public static GitRepository Create()
        {
            var directory = new TemporaryDirectory();
            var repository = new GitRepository(directory);

            try
            {
                repository.Git("init", "-b", "main");
                repository.Git("config", "user.name", "NovellMaker Tests");
                repository.Git("config", "user.email", "tests@novellmaker.invalid");
                repository.Git("config", "core.autocrlf", "false");
                return repository;
            }
            catch
            {
                repository.Dispose();
                throw;
            }
        }

        public string CreateDirectory(string relativePath)
        {
            var path = System.IO.Path.Combine(Root, FromGitPath(relativePath));
            Directory.CreateDirectory(path);
            return path;
        }

        public void Write(string relativePath, string contents)
        {
            var path = System.IO.Path.Combine(Root, FromGitPath(relativePath));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents, Utf8WithoutBom);
        }

        public string WriteExecutableShellScript(string relativePath, string body)
        {
            var path = System.IO.Path.Combine(Root, FromGitPath(relativePath));
            File.WriteAllText(path, $"#!/bin/sh\n{body}", Utf8WithoutBom);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }

            return path;
        }

        public void CommitAll(string message)
        {
            Git("add", "--all");
            Git("commit", "-m", message);
        }

        public void Git(params string[] arguments)
        {
            var result = GitAllowFailure(arguments);
            if (result.ExitCode != 0)
            {
                Assert.Fail($"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.StandardError}");
            }
        }

        public GitResult GitAllowFailure(params string[] arguments)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = Root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                },
            };

            process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Git test setup timed out.");
            }

            return new(process.ExitCode, standardOutput, standardError);
        }

        public void Dispose() => _directory.Dispose();

        private static string FromGitPath(string path) =>
            path.Replace('/', System.IO.Path.DirectorySeparatorChar);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NovellMaker.GitTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(Path))
                    {
                        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }

                        Directory.Delete(Path, recursive: true);
                    }

                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    private sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);
}
