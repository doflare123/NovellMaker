using System.Collections.ObjectModel;
using System.Collections.Immutable;
using NovellMaker.Core.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed partial class EditorSession
{
    private AssetLibraryDocumentViewModel? assetLibrary;
    private ImageAssetViewModel? selectedImage;
    private string assetFilter = "";
    private int assetRevision;
    private ImmutableArray<ImageAsset> synchronizedImages;
    private string? synchronizedDirectory;
    private int synchronizedRevision = -1;

    public ObservableCollection<ImageAssetViewModel> Images { get; } = [];
    public ObservableCollection<ImageAssetViewModel> FilteredImages { get; } = [];
    public AsyncCommand DeleteImageCommand { get; private set; } = null!;
    public AsyncCommand ImportImageCommand { get; private set; } = null!;
    public AsyncCommand OpenAssetLibraryCommand { get; private set; } = null!;
    public string? ProjectDirectory => projectPath is null ? null : Path.GetDirectoryName(projectPath);
    public int AssetRevision => assetRevision;
    public string AssetLibraryTitle => "Изображения" + (project is not null &&
        (savedProject is null ? project.Images.Length > 0 : !project.Images.SequenceEqual(savedProject.Images)) ? " *" : "");
    public string AssetSearchSummary => $"Найдено: {FilteredImages.Count} из {Images.Count}";

    public string AssetFilter
    {
        get => assetFilter;
        set
        {
            value ??= "";
            if (assetFilter == value) return;
            assetFilter = value;
            Changed();
            ApplyAssetFilter();
        }
    }

    public ImageAssetViewModel? SelectedImage
    {
        get => selectedImage;
        set
        {
            if (ReferenceEquals(value, selectedImage)) return;
            selectedImage = value;
            EndEditGroup();
            NotifyImageSelection();
        }
    }

    public string SelectedImageName
    {
        get => selectedImage?.Name ?? "";
        set
        {
            value ??= "";
            if (project is null || selectedImage is null || selectedImage.Name == value) return;
            // Keep the edited row visible even if its new name no longer matches the search.
            if (assetFilter.Length > 0) AssetFilter = "";
            var index = project.Images.IndexOf(selectedImage.Model);
            if (index >= 0) EditProject(project with { Images = project.Images.SetItem(index, selectedImage.Model with { Name = value }) }, $"image-name:{selectedImage.Id}");
        }
    }

    public bool CanEditImage => CanEdit && selectedImage is not null;
    public string SelectedImageValidation => selectedImage is not null && string.IsNullOrWhiteSpace(selectedImage.Name) ? "Введите название изображения." : "";
    public string SelectedImageInfo => selectedImage is null ? "Выберите изображение в библиотеке." :
        $"{selectedImage.Dimensions}\n{selectedImage.RelativePath}\n{selectedImage.Id}";
    public string SelectedImageStatus => selectedImage?.Status ?? "";

    private void InitializeAssets()
    {
        DeleteImageCommand = new(() => selectedImage is { } image ? DeleteImageAsync(image.Id) : Task.CompletedTask, () => CanEditImage);
        ImportImageCommand = new(ImportImageAsync, () => CanEdit);
        OpenAssetLibraryCommand = Command(OpenAssetLibrary, () => CanEdit);
    }

    public Task ImportImageAsync() => ImportImageCoreAsync(false);
    public Task ImportDesignerImageAsync() => ImportImageCoreAsync(true);
    private Task ImportImageCoreAsync(bool preserveDocument) => RunAsync(async () =>
    {
        if (project is null) return;
        var source = await dialogs.PickImageAsync();
        if (source is null) return;
        if (projectPath is null && !await SaveCoreAsync(false)) return;
        var image = await imageAssets.ImportAsync(ProjectDirectory!, source);
        EditProject(project with { Images = project.Images.Add(image) });
        AssetFilter = "";
        SelectedImage = Images.First(item => item.Id == image.Id);
        if (!preserveDocument) OpenAssetLibrary();
        Log($"Изображение «{image.Name}» импортировано.");
        await UpdateGitAsync();
    });

    public void OpenAssetLibrary()
    {
        if (!HasProject) return;
        assetLibrary ??= new AssetLibraryDocumentViewModel(this);
        if (!Documents.Contains(assetLibrary)) Documents.Add(assetLibrary);
        SelectedDocument = assetLibrary;
    }

    public void SelectImage(Guid imageId)
    {
        AssetFilter = "";
        OpenAssetLibrary();
        SelectedImage = Images.FirstOrDefault(image => image.Id == imageId);
    }

    public void CloseAssetLibrary()
    {
        if (IsBusy || assetLibrary is null) return;
        Documents.Remove(assetLibrary);
        if (ReferenceEquals(SelectedDocument, assetLibrary)) SelectedDocument = Documents.FirstOrDefault();
        assetLibrary = null;
    }

    private void ResetAssets()
    {
        assetLibrary = null;
        selectedImage = null;
        assetFilter = "";
        Images.Clear();
        FilteredImages.Clear();
        assetRevision++;
    }

    private void NotifyAssetsChanged()
    {
        var models = project?.Images ?? [];
        if (synchronizedImages != models || synchronizedDirectory != ProjectDirectory || synchronizedRevision != assetRevision)
        {
            synchronizedImages = models;
            synchronizedDirectory = ProjectDirectory;
            synchronizedRevision = assetRevision;
            var rows = Images.ToDictionary(image => image.Id);
            var ids = models.Select(image => image.Id).ToHashSet();
            for (var index = Images.Count - 1; index >= 0; index--)
                if (!ids.Contains(Images[index].Id)) Images.RemoveAt(index);
            for (var index = 0; index < models.Length; index++)
            {
                var model = models[index];
                if (!rows.TryGetValue(model.Id, out var row))
                {
                    row = new ImageAssetViewModel(model);
                    Images.Insert(index, row);
                }
                else if (!ReferenceEquals(Images[index], row)) Images.Move(Images.IndexOf(row), index);
                row.Refresh(model, ProjectDirectory, assetRevision);
            }
            if (selectedImage is not null && !Images.Contains(selectedImage)) SelectedImage = null;
            ApplyAssetFilter();
            NotifyImageSelection();
        }
        assetLibrary?.RefreshHeader();
        foreach (var property in new[] { nameof(ProjectDirectory), nameof(AssetFilter), nameof(AssetRevision), nameof(AssetLibraryTitle) }) Changed(property);
        NotifyAssetCommands();
    }

    private void ApplyAssetFilter()
    {
        var query = assetFilter.Trim();
        var wanted = Images.Where(image => query.Length == 0 || image.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            image.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var wantedSet = wanted.ToHashSet();
        for (var index = FilteredImages.Count - 1; index >= 0; index--)
            if (!wantedSet.Contains(FilteredImages[index])) FilteredImages.RemoveAt(index);
        for (var index = 0; index < wanted.Length; index++)
        {
            if (index < FilteredImages.Count && ReferenceEquals(FilteredImages[index], wanted[index])) continue;
            var oldIndex = FilteredImages.IndexOf(wanted[index]);
            if (oldIndex >= 0) FilteredImages.Move(oldIndex, index);
            else FilteredImages.Insert(index, wanted[index]);
        }
        Changed(nameof(AssetSearchSummary));
    }

    private void RefreshAssetFiles()
    {
        assetRevision++;
        NotifyProjectChanged();
    }

    private void NotifyImageSelection()
    {
        DeleteImageCommand?.NotifyCanExecuteChanged();
        foreach (var property in new[] { nameof(SelectedImage), nameof(SelectedImageName), nameof(CanEditImage),
                     nameof(SelectedImageValidation), nameof(SelectedImageInfo), nameof(SelectedImageStatus) }) Changed(property);
    }

    private void NotifyAssetCommands()
    {
        DeleteImageCommand?.NotifyCanExecuteChanged();
        ImportImageCommand?.NotifyCanExecuteChanged();
        OpenAssetLibraryCommand?.NotifyCanExecuteChanged();
        assetLibrary?.CloseCommand.NotifyCanExecuteChanged();
        Changed(nameof(CanEditImage));
    }
}
