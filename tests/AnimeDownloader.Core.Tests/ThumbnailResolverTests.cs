using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Tests;

public class ThumbnailResolverTests
{
    [Fact]
    public void ResolveThumbnailUrl_PrefersNativeThumbnail()
    {
        const string original = "https://example.com/img/original.png";
        const string thumb = "https://example.com/img/thumb.jpg";

        Assert.Equal(thumb, ThumbnailResolver.ResolveThumbnailUrl(thumb, original));
    }

    [Fact]
    public void ResolveThumbnailUrl_FallsBackToOriginalWithoutTemplate()
    {
        const string original = "https://example.com/img/a-big-image.jpg";

        Assert.Equal(original, ThumbnailResolver.ResolveThumbnailUrl(null, original));
    }

    [Fact]
    public void ResolveThumbnailUrl_AppliesProxyTemplateWhenProvided()
    {
        const string original = "https://example.com/img/a.jpg";
        const string template = "https://images.weserv.nl/?url={url}&w=480&fit=cover";

        var resolved = ThumbnailResolver.ResolveThumbnailUrl(null, original, template);

        Assert.NotNull(resolved);
        Assert.StartsWith("https://images.weserv.nl/?url=", resolved);
        Assert.Contains(Uri.EscapeDataString(original), resolved);
        Assert.DoesNotContain("{url}", resolved);
    }

    [Fact]
    public void ApplyProxyTemplate_ReturnsNullWhenPlaceholderMissing()
    {
        Assert.Null(ThumbnailResolver.ApplyProxyTemplate("https://example.com/no-placeholder", "https://x/y.jpg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("data:image/png;base64,AAAA")]
    public void ResolveThumbnailUrl_ReturnsNullForUnusableInput(string? original)
    {
        Assert.Null(ThumbnailResolver.ResolveThumbnailUrl(null, original));
    }
}
