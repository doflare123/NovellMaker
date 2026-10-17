using System.ComponentModel;
using NovellMaker.Core.Assets;

namespace NovellMaker.Editor.ViewModels;

public sealed class ImageAssetViewModel(ImageAsset model) : INotifyPropertyChanged
{
    private bool initialized;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ImageAsset Model { get; private set; } = model;
    public Guid Id => Model.Id;
    public string Name => Model.Name;
    public string RelativePath => Model.RelativePath;
    public string Dimensions => $"{Model.PixelWidth} × {Model.PixelHeight}";
    public string? ProjectDirectory { get; private set; }
    public string Status { get; private set; } = "";
    public int Revision { get; private set; }

    internal void Refresh(ImageAsset value, string? directory, int revision)
    {
        if (initialized && ReferenceEquals(Model, value) && ProjectDirectory == directory && Revision == revision) return;
        initialized = true;
        Model = value;
        ProjectDirectory = directory;
        Revision = revision;
        Status = ImageFileStatus(directory, value);
        PropertyChanged?.Invoke(this, new(null));
    }

    internal static string ImageFileStatus(string? directory, ImageAsset image)
    {
        if (directory is null) return "Сначала сохраните проект.";
        try
        {
            return File.Exists(AssetPath.Resolve(directory, image.RelativePath)) ? "" : $"Файл «{image.Name}» не найден: {image.RelativePath}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Изображение недоступно: {error.Message}";
        }
    }
}
