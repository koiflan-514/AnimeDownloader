using AnimeDownloader.Core.Models;

namespace AnimeDownloader.Core.Services;

/// <summary>标签联想 / 相关标签的单条结果。</summary>
/// <param name="Name">标签原始名（提交查询时使用）。</param>
/// <param name="PostCount">该标签的帖子数（可能为 null）。</param>
/// <param name="Category">标签类别（图源提供时填充）。</param>
/// <param name="ChineseName">中文译名（内嵌热门标签表命中时填充）。</param>
public sealed record TagSuggestion(
    string Name,
    long? PostCount = null,
    TagCategory? Category = null,
    string? ChineseName = null);

/// <summary>
/// 支持标签联想的图源可选实现的接口：输入前缀 → 候选标签（含热度与类别），
/// 以及某标签的相关标签推荐（Danbooru / Moebooru 系提供）。
/// </summary>
public interface ITagSuggester
{
    /// <summary>图源是否提供标签联想（还取决于凭据等运行时条件时也应如实反映）。</summary>
    bool SupportsTagSuggestions { get; }

    /// <summary>按输入前缀联想候选标签，按热度排序。</summary>
    Task<IReadOnlyList<TagSuggestion>> SuggestTagsAsync(
        string input,
        int limit = 15,
        CancellationToken cancellationToken = default);

    /// <summary>返回与指定标签相关的标签；图源不支持时返回空列表。</summary>
    Task<IReadOnlyList<TagSuggestion>> GetRelatedTagsAsync(
        string tag,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 联想的本地兜底：图源联想接口不可用（网络波动、无凭据、站点不支持前缀查询）时，
/// 用内嵌热门标签表做前缀匹配，保证输入时始终有候选弹出。
/// </summary>
public static class TagSuggesterFallback
{
    /// <summary>按前缀在本地热门标签表中联想，按帖子数降序。</summary>
    public static IReadOnlyList<TagSuggestion> LocalPrefix(string prefix, int limit)
    {
        var matches = TagLocalization.SearchByPrefix(prefix, Math.Clamp(limit, 1, 30));
        var suggestions = new List<TagSuggestion>(matches.Count);
        foreach (var match in matches)
        {
            suggestions.Add(new TagSuggestion(
                match.Name,
                match.Entry.PostCount,
                FromInt(match.Entry.Category),
                match.Entry.ChineseName));
        }

        return suggestions;
    }

    private static TagCategory? FromInt(int value) => value switch
    {
        0 => TagCategory.General,
        1 => TagCategory.Artist,
        2 => TagCategory.Circle,
        3 => TagCategory.Copyright,
        4 => TagCategory.Character,
        5 => TagCategory.Meta,
        _ => null,
    };
}
