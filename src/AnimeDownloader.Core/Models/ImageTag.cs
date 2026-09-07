namespace AnimeDownloader.Core.Models;

/// <summary>
/// Danbooru / Moebooru 系标签类别（与 API 返回的 category/type 数值一致）。
/// Gelbooru 系的 2=copyright、3=character、4=meta 由各适配器先行映射后再转为本枚举。
/// </summary>
public enum TagCategory
{
    General = 0,
    Artist = 1,
    Circle = 2,
    Copyright = 3,
    Character = 4,
    Meta = 5,
}

/// <summary>
/// 图片上的单个标签。<see cref="Category"/> 在图源提供类别时填充（Danbooru 直接提供；
/// Moebooru / Gelbooru 的帖子负载不含类别，由联想接口按需补充）。
/// </summary>
public sealed record ImageTag(string Name, TagCategory? Category = null);
