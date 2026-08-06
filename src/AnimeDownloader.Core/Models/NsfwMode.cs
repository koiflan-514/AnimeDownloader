namespace AnimeDownloader.Core.Models;

/// <summary>
/// NSFW 内容过滤三态，对应参考项目 CatgirlDownloader 的 NSFWOption。
/// </summary>
public enum NsfwMode
{
    /// <summary>显示全部内容（不过滤）。</summary>
    ShowEverything,

    /// <summary>仅显示 NSFW 内容。</summary>
    OnlyNsfw,

    /// <summary>屏蔽 NSFW 内容（默认）。</summary>
    BlockNsfw,
}

public static class NsfwModeExtensions
{
    /// <summary>将枚举转换为持久化字符串（与参考项目的存储值一致）。</summary>
    public static string ToStorageString(this NsfwMode mode) => mode switch
    {
        NsfwMode.ShowEverything => "Show everything",
        NsfwMode.OnlyNsfw => "Only NSFW",
        NsfwMode.BlockNsfw => "Block NSFW",
        _ => "Block NSFW",
    };

    /// <summary>从持久化字符串解析枚举；无法识别时回退到 BlockNsfw。</summary>
    public static NsfwMode FromStorageString(string? value) => value switch
    {
        "Show everything" => NsfwMode.ShowEverything,
        "Only NSFW" => NsfwMode.OnlyNsfw,
        "Block NSFW" => NsfwMode.BlockNsfw,
        _ => NsfwMode.BlockNsfw,
    };
}
