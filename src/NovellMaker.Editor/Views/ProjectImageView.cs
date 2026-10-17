using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NovellMaker.Core.Assets;
using NovellMaker.Editor.Services.Assets;

namespace NovellMaker.Editor.Views;

public sealed class ProjectImageView : UserControl
{
    public static readonly StyledProperty<string?> ProjectDirectoryProperty =
        AvaloniaProperty.Register<ProjectImageView, string?>(nameof(ProjectDirectory));

    public static readonly StyledProperty<ImageAsset?> AssetProperty =
        AvaloniaProperty.Register<ProjectImageView, ImageAsset?>(nameof(Asset));

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<ProjectImageView, int>(nameof(Revision));

    public static readonly StyledProperty<int> MaximumWidthProperty =
        AvaloniaProperty.Register<ProjectImageView, int>(nameof(MaximumWidth), 1280);

    public static readonly DirectProperty<ProjectImageView, string> PreviewStatusProperty =
        AvaloniaProperty.RegisterDirect<ProjectImageView, string>(
            nameof(PreviewStatus),
            view => view.PreviewStatus);

    public static readonly DirectProperty<ProjectImageView, bool> HasImageProperty =
        AvaloniaProperty.RegisterDirect<ProjectImageView, bool>(
            nameof(HasImage),
            view => view.HasImage);

    private readonly IImageAssetService imageAssets;
    private readonly Image image;
    private readonly TextBlock statusText;
    private CancellationTokenSource? loadCancellation;
    private Bitmap? bitmap;
    private long loadVersion;
    private bool attached;
    private string previewStatus = "Изображение не выбрано.";
    private bool hasImage;
    internal Task PreviewLoad { get; private set; } = Task.CompletedTask;

    public string? ProjectDirectory
    {
        get => GetValue(ProjectDirectoryProperty);
        set => SetValue(ProjectDirectoryProperty, value);
    }

    public ImageAsset? Asset
    {
        get => GetValue(AssetProperty);
        set => SetValue(AssetProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public int MaximumWidth
    {
        get => GetValue(MaximumWidthProperty);
        set => SetValue(MaximumWidthProperty, value);
    }

    public string PreviewStatus
    {
        get => previewStatus;
        private set => SetAndRaise(PreviewStatusProperty, ref previewStatus, value);
    }

    public bool HasImage
    {
        get => hasImage;
        private set => SetAndRaise(HasImageProperty, ref hasImage, value);
    }

    public ProjectImageView()
        : this(new ImageAssetService())
    {
    }

    internal ProjectImageView(IImageAssetService imageAssets)
    {
        this.imageAssets = imageAssets;
        image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        statusText = new TextBlock
        {
            Text = previewStatus,
            Foreground = new SolidColorBrush(Color.Parse("#AEB4BD")),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
        };

        var panel = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#1B1D21")),
            ClipToBounds = true,
            Children = { image, statusText },
        };
        Content = panel;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProjectDirectoryProperty ||
            change.Property == AssetProperty ||
            change.Property == RevisionProperty ||
            change.Property == MaximumWidthProperty)
        {
            BeginLoad();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        attached = true;
        BeginLoad();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        attached = false;
        CancelPendingLoad();
        ReplaceBitmap(null);
        SetState("Изображение не выбрано.", false);
        base.OnDetachedFromVisualTree(args);
    }

    private void BeginLoad()
    {
        CancelPendingLoad();
        var version = ++loadVersion;
        ReplaceBitmap(null);

        if (!attached)
        {
            return;
        }

        var directory = ProjectDirectory;
        var asset = Asset;
        if (directory is null || asset is null)
        {
            SetState("Изображение не выбрано.", false);
            return;
        }

        var cancellation = new CancellationTokenSource();
        loadCancellation = cancellation;
        SetState("Загрузка изображения…", false);
        PreviewLoad = LoadPreviewAsync(directory, asset, MaximumWidth, version, cancellation);
    }

    private async Task LoadPreviewAsync(
        string directory,
        ImageAsset asset,
        int maximumWidth,
        long version,
        CancellationTokenSource cancellation)
    {
        Bitmap? loadedBitmap = null;
        try
        {
            var bytes = await imageAssets
                .ReadPreviewAsync(directory, asset, maximumWidth, cancellation.Token)
                .ConfigureAwait(false);
            loadedBitmap = await Task.Run(
                    () =>
                    {
                        using var stream = new MemoryStream(bytes, writable: false);
                        return new Bitmap(stream);
                    },
                    cancellation.Token)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!attached || version != loadVersion || cancellation.IsCancellationRequested)
                {
                    loadedBitmap.Dispose();
                    loadedBitmap = null;
                    return;
                }

                ReplaceBitmap(loadedBitmap);
                loadedBitmap = null;
                SetState("Изображение загружено.", true);
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            loadedBitmap?.Dispose();
        }
        catch (Exception exception)
        {
            loadedBitmap?.Dispose();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (attached && version == loadVersion)
                {
                    ReplaceBitmap(null);
                    SetState($"Не удалось загрузить изображение: {exception.Message}", false);
                }
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(loadCancellation, cancellation))
                {
                    loadCancellation = null;
                }
            });
            cancellation.Dispose();
        }
    }

    private void CancelPendingLoad()
    {
        loadVersion++;
        loadCancellation?.Cancel();
        loadCancellation = null;
    }

    private void ReplaceBitmap(Bitmap? replacement)
    {
        image.Source = replacement;
        var previous = bitmap;
        bitmap = replacement;
        previous?.Dispose();
    }

    private void SetState(string status, bool imageAvailable)
    {
        PreviewStatus = status;
        HasImage = imageAvailable;
        statusText.Text = status;
        statusText.IsVisible = !imageAvailable;
    }
}
