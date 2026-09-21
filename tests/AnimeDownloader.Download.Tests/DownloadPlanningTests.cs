using AnimeDownloader.Download.Contracts;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Download.Tests;

/// <summary>
/// 文件名净化 / 扩展名 / MIME 推断的测试。
/// </summary>
/// <remarks>
/// 这几条规则看着琐碎，但每一条都对应一个真机上会暴露的失败：
/// 文件名带 <c>/</c> 会让 MediaStore 直接拒绝插入；超长名字在 ext4 上会被静默截断成
/// 「看不到扩展名的一坨」；批内重名会让第二张覆盖第一张 —— 用户看到的是「少了一张」。
/// </remarks>
public sealed class DownloadFileNamingTests
{
    [Theory]
    [InlineData("a/b.png", "a_b.png")]
    [InlineData("a\\b.png", "a_b.png")]
    [InlineData("a:b*c?d\"e<f>g|h.png", "a_b_c_d_e_f_g_h.png")]
    [InlineData("a\tb.png", "a_b.png")]
    public void Sanitize_替换非法字符(string input, string expected) =>
        Assert.Equal(expected, DownloadFileNaming.Sanitize(input));

    [Fact]
    public void Sanitize_压掉重复空格() =>
        Assert.Equal("a b.png", DownloadFileNaming.Sanitize("a    b.png"));

    [Fact]
    public void Sanitize_去掉首尾的点与空格()
    {
        // Windows 与部分 MTP 实现都不接受以点结尾的文件名。
        Assert.Equal("demo.png", DownloadFileNaming.Sanitize("  ..demo.png..  "));
    }

    [Fact]
    public void Sanitize_空输入回落到时间戳文件名()
    {
        var name = DownloadFileNaming.Sanitize("   ");
        Assert.StartsWith("image_", name, StringComparison.Ordinal);
        Assert.EndsWith(".png", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_超长名截断后仍保留扩展名()
    {
        var name = DownloadFileNaming.Sanitize(new string('x', 400) + ".jpeg");
        Assert.Equal(DownloadFileNaming.MaxNameLength, name.Length);
        Assert.EndsWith(".jpeg", name, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureExtension_无扩展名时补兜底值() =>
        Assert.Equal("post.png", DownloadFileNaming.EnsureExtension("post"));

    [Fact]
    public void EnsureExtension_已有扩展名时原样返回() =>
        Assert.Equal("post.jpg", DownloadFileNaming.EnsureExtension("post.jpg"));

    [Theory]
    [InlineData("a.php?i=1", null)]
    [InlineData("a.thisiswaytoolong", null)]
    [InlineData("noext", null)]
    [InlineData("a.PNG", "png")]
    public void GetExtension_只接受像扩展名的尾巴(string input, string? expected) =>
        Assert.Equal(expected, DownloadFileNaming.GetExtension(input));

    [Theory]
    [InlineData("a.jpg", "image/jpeg")]
    [InlineData("a.jpeg", "image/jpeg")]
    [InlineData("a.PNG", "image/png")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.avif", "image/avif")]
    [InlineData("a.php", null)]
    [InlineData("a", null)]
    public void InferMimeType_按扩展名映射(string input, string? expected) =>
        Assert.Equal(expected, DownloadFileNaming.InferMimeType(input));

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.mp4", false)]
    [InlineData("a.php", false)]
    public void LooksLikeImage_只认图片类型(string input, bool expected) =>
        Assert.Equal(expected, DownloadFileNaming.LooksLikeImage(input));
}

/// <summary>
/// 批量规划：一一对应、批内去重、扩展名保留。
/// </summary>
public sealed class DownloadPlannerTests
{
    private static ImageItem Item(string url, string? id = null, string? ext = null) =>
        new(url, Id: id, Extension: ext);

    [Fact]
    public void Plan_与入参一一对应且顺序不变()
    {
        var items = new[] { Item("https://x/1.jpg", "1"), Item("https://x/2.png", "2") };
        var specs = DownloadPlanner.Plan(items);

        Assert.Equal(2, specs.Count);
        Assert.Equal("1.jpg", specs[0].DisplayName);
        Assert.Equal("2.png", specs[1].DisplayName);
        Assert.Equal("https://x/1.jpg", specs[0].SourceUrl);
    }

    [Fact]
    public void Plan_重名时插入序号而不是覆盖()
    {
        var items = new[] { Item("https://x/a.jpg", "same"), Item("https://x/b.jpg", "same") };
        var specs = DownloadPlanner.Plan(items);

        Assert.Equal("same.jpg", specs[0].DisplayName);
        Assert.Equal("same (2).jpg", specs[1].DisplayName);
    }

    [Fact]
    public void Plan_大小写不同也算重名()
    {
        // Android 的外部存储多是大小写不敏感的（FAT/exFAT、部分 MTP 实现），
        // 只按序数比较会让 A.png 与 a.png 落到同一个文件上。
        var items = new[] { Item("https://x/A.png", "A"), Item("https://x/a.png", "a") };
        var specs = DownloadPlanner.Plan(items);

        Assert.NotEqual(specs[0].DisplayName, specs[1].DisplayName, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_第三次重名继续递增()
    {
        var items = new[]
        {
            Item("https://x/1.jpg", "d"),
            Item("https://x/2.jpg", "d"),
            Item("https://x/3.jpg", "d"),
        };
        var specs = DownloadPlanner.Plan(items);

        Assert.Equal("d.jpg", specs[0].DisplayName);
        Assert.Equal("d (2).jpg", specs[1].DisplayName);
        Assert.Equal("d (3).jpg", specs[2].DisplayName);
    }

    [Fact]
    public void Plan_沿用图源给的扩展名并推断出MIME()
    {
        var specs = DownloadPlanner.Plan([Item("https://x/noext", "k", "webp")]);

        Assert.Equal("k.webp", specs[0].DisplayName);
        Assert.Equal("image/webp", specs[0].MimeType);
    }

    [Fact]
    public void Plan_无法推断MIME时留空交给系统判定()
    {
        var specs = DownloadPlanner.Plan([Item("https://x/noext", "k", "php")]);

        Assert.Equal("k.php", specs[0].DisplayName);
        Assert.Null(specs[0].MimeType);
    }

    [Fact]
    public void Plan_相册名可覆盖且空值回落默认()
    {
        var items = new[] { Item("https://x/a.jpg", "a") };

        Assert.Equal("MyAlbum", DownloadPlanner.Plan(items, "MyAlbum")[0].AlbumName);
        Assert.Equal(
            DownloadPlanner.DefaultAlbumName,
            DownloadPlanner.Plan(items, "   ")[0].AlbumName);
    }

    [Fact]
    public void Plan_空列表返回空()
    {
        Assert.Empty(DownloadPlanner.Plan(Array.Empty<ImageItem>()));
    }

    [Fact]
    public void Plan_净化路径分隔符防止越出相册目录()
    {
        var specs = DownloadPlanner.Plan([Item("https://x/e.jpg", "../../etc/passwd")]);
        var name = specs[0].DisplayName;

        // 关键性质是「名字里不再有路径分隔符」，于是它只能落在相册目录内 ——
        // 名字里残留的 ".." 本身无害（没有分隔符就只是一个含点的普通文件名）。
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.Equal(name, Path.GetFileName(name));
    }
}
