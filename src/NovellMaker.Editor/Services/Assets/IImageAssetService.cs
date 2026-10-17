using NovellMaker.Core.Assets;

namespace NovellMaker.Editor.Services.Assets;

public interface IImageAssetService
{
    Task<ImageAsset> ImportAsync(
        string projectDirectory,
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task CopyAsync(
        string sourceProjectDirectory,
        string targetProjectDirectory,
        IReadOnlyList<ImageAsset> images,
        CancellationToken cancellationToken = default);

    Task<byte[]> ReadPreviewAsync(
        string projectDirectory,
        ImageAsset image,
        int maximumWidth = 1280,
        CancellationToken cancellationToken = default);
}
