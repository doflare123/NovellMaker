namespace NovellMaker.Editor.Services.Git;

public interface IGitStatusService
{
    Task<GitStatusSnapshot> GetStatusAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default);
}
