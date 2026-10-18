using System.ComponentModel;
using NovellMaker.Core.Assets;
using NovellMaker.Core.Characters;

namespace NovellMaker.Editor.ViewModels;

public sealed class CharacterStateViewModel(CharacterState model) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public CharacterState Model { get; private set; } = model;
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public Guid ImageId => Model.ImageId;
    public ImageAsset? Image { get; private set; }
    public string ImageName => Image?.Name ?? "Отсутствующее изображение";
    public string? ProjectDirectory { get; private set; }
    public int Revision { get; private set; }
    public string Status { get; private set; } = "";

    internal void Refresh(CharacterState value, IReadOnlyCollection<ImageAsset> images, string? projectDirectory, int revision)
    {
        var image = images.FirstOrDefault(item => item.Id == value.ImageId);
        var status = image is null
            ? $"Изображение состояния не найдено: {value.ImageId}"
            : ImageAssetViewModel.ImageFileStatus(projectDirectory, image);
        if (ReferenceEquals(Model, value) && ReferenceEquals(Image, image) &&
            ProjectDirectory == projectDirectory && Revision == revision && Status == status)
        {
            return;
        }

        Model = value;
        Image = image;
        ProjectDirectory = projectDirectory;
        Revision = revision;
        Status = status;
        PropertyChanged?.Invoke(this, new(null));
    }
}
