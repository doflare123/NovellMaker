using NovellMaker.Core.Projects;

namespace NovellMaker.Editor.Services.Preview;

public sealed record PreviewRequest(NovelProject Project, string AssetsRoot, Guid SceneId, Guid? LineId, string Language);

public interface IPreviewService : IDisposable
{
    Task StartAsync(PreviewRequest request, Action<string> report);
}
