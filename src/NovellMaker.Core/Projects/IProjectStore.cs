namespace NovellMaker.Core.Projects;

public interface IProjectStore
{
    Task<NovelProject> LoadAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string path,
        NovelProject project,
        CancellationToken cancellationToken = default);
}
