using System.Collections.Immutable;
using NovellMaker.Core.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class SceneDocumentViewModel
{
    private ImmutableArray<ImageAsset> backgroundCatalog;
    private Guid? sampledBackground;
    private string? backgroundDirectory;
    private int backgroundRevision = -1;
    private bool refreshingBackground;

    public IReadOnlyList<BackgroundImageOption> BackgroundOptions { get; private set; } = [];
    public ImageAsset? BackgroundImage { get; private set; }
    public string BackgroundStatus { get; private set; } = "";

    public Guid? BackgroundImageId
    {
        get => scene.BackgroundImageId;
        set
        {
            if (Session.CanEdit && value != scene.BackgroundImageId)
                Session.UpdateScene(scene with { BackgroundImageId = value });
        }
    }

    public BackgroundImageOption? SelectedBackground
    {
        get => BackgroundOptions.FirstOrDefault(option => option.Id == BackgroundImageId);
        set
        {
            // A selector can temporarily clear selection when its catalog changes.
            // Only the explicit "Без задника" option removes a scene reference.
            if (!refreshingBackground && value is not null) BackgroundImageId = value.Id;
        }
    }

    private void RefreshBackground()
    {
        var catalog = Session.Project?.Images ?? [];
        var catalogChanged = backgroundCatalog != catalog;
        var selectionChanged = sampledBackground != BackgroundImageId;
        if (!catalogChanged && !selectionChanged && backgroundDirectory == Session.ProjectDirectory && backgroundRevision == Session.AssetRevision) return;
        backgroundCatalog = catalog;
        sampledBackground = BackgroundImageId;
        backgroundDirectory = Session.ProjectDirectory;
        backgroundRevision = Session.AssetRevision;
        refreshingBackground = true;
        try
        {
            BackgroundImage = catalog.FirstOrDefault(image => image.Id == BackgroundImageId);
            BackgroundStatus = BackgroundImageId is null ? "Выберите изображение из библиотеки." :
                BackgroundImage is null ? $"Изображение {BackgroundImageId} отсутствует в библиотеке. Выберите другой задник." :
                ImageAssetViewModel.ImageFileStatus(backgroundDirectory, BackgroundImage);
            var missingReference = BackgroundImageId.HasValue && BackgroundImage is null;
            if (catalogChanged || BackgroundOptions.Count != catalog.Length + 1 + (missingReference ? 1 : 0) ||
                !BackgroundOptions.Any(option => option.Id == BackgroundImageId))
            {
                var options = new List<BackgroundImageOption> { new(null, "Без задника") };
                options.AddRange(catalog.Select(image => new BackgroundImageOption(image.Id, image.Name)));
                if (BackgroundImageId is { } missing && BackgroundImage is null)
                    options.Add(new(missing, $"Отсутствует: {missing}"));
                BackgroundOptions = options;
                Changed(nameof(BackgroundOptions));
            }
            foreach (var property in new[] { nameof(BackgroundImageId), nameof(BackgroundImage), nameof(BackgroundStatus), nameof(SelectedBackground) })
                Changed(property);
        }
        finally { refreshingBackground = false; }
    }
}

public sealed record BackgroundImageOption(Guid? Id, string Name);
