using NovellMaker.Core.Assets;

namespace NovellMaker.Core.Tests.Assets;

[TestClass]
public sealed class ImageAssetTests
{
    [TestMethod]
    public void Constructor_AcceptsMaximumSupportedPixelCount()
    {
        var asset = new ImageAsset(
            Guid.NewGuid(),
            "Панорама",
            "Assets/Images/panorama.png",
            8000,
            4000);

        ImageAsset.ValidateForPersistence(asset);
        Assert.AreEqual(32_000_000L, (long)asset.PixelWidth * asset.PixelHeight);
    }

    [TestMethod]
    [DataRow(0, 100)]
    [DataRow(100, 0)]
    [DataRow(8193, 100)]
    [DataRow(100, 8193)]
    [DataRow(8001, 4000)]
    public void Constructor_RejectsUnsupportedDimensions(int width, int height)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageAsset(
            Guid.NewGuid(),
            "Изображение",
            "Assets/Images/image.png",
            width,
            height));
    }

    [TestMethod]
    public void ValidateForPersistence_RejectsInvalidEditableCopy()
    {
        var valid = new ImageAsset(
            Guid.NewGuid(),
            "Изображение",
            "Assets/Images/image.png",
            1920,
            1080);

        Assert.ThrowsExactly<ArgumentException>(() =>
            ImageAsset.ValidateForPersistence(valid with { Name = " " }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ImageAsset.ValidateForPersistence(valid with { RelativePath = "../outside.png" }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            ImageAsset.ValidateForPersistence(valid with { PixelWidth = 9000 }));
    }
}
