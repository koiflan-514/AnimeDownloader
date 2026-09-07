using System.Text.Json;

namespace AnimeDownloader.Core.Services;

/// <summary>
/// 热门标签的中文本土化查询：内嵌 Danbooru 热门标签中英对照表（按帖子数排序的前 5000 个，
/// 数据来源：github.com/ffdkj/ffdkj-Danbooru_Tag-Chinese-English-Translation-Table，CC0）。
/// 查不到的标签返回 false，UI 回退显示原始标签名。
/// </summary>
public static class TagLocalization
{
    private const string ResourceName = "AnimeDownloader.Core.Resources.tag_zh.json";

    private static IReadOnlyDictionary<string, TagLocalizationEntry>? _table;

    private static IReadOnlyDictionary<string, TagLocalizationEntry> Table
    {
        get
        {
            if (_table is null)
            {
                var loaded = LoadEmbeddedTable();
                _table = loaded;
            }

            return _table;
        }
    }

    /// <summary>查询标签的中文译名与帖子数。</summary>
    public static bool TryGet(string tag, out TagLocalizationEntry entry)
    {
        entry = default;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        return Table.TryGetValue(tag.Trim().ToLowerInvariant(), out entry);
    }

    /// <summary>
    /// 按前缀在本地热门标签表中联想候选（按帖子数降序）。
    /// 用于图源联想接口不可用（无凭据 / 网络 / 站点不支持前缀查询）时的离线兜底。
    /// </summary>
    public static IReadOnlyList<TagLocalizationMatch> SearchByPrefix(string prefix, int limit)
    {
        var result = new List<TagLocalizationMatch>();
        if (string.IsNullOrWhiteSpace(prefix) || limit <= 0)
        {
            return result;
        }

        var trimmed = prefix.Trim();
        foreach (var pair in Table)
        {
            if (!pair.Key.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new TagLocalizationMatch(pair.Key, pair.Value));
            if (result.Count >= limit * 4)
            {
                break;
            }
        }

        return result
            .OrderByDescending(m => m.Entry.PostCount)
            .Take(limit)
            .ToList();
    }

    private static Dictionary<string, TagLocalizationEntry> LoadEmbeddedTable()
    {
        var assembly = typeof(TagLocalization).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return new Dictionary<string, TagLocalizationEntry>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(stream);
        var table = new Dictionary<string, TagLocalizationEntry>(StringComparer.Ordinal);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            // 行格式：[name, cn_name, category, post_count]
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 4)
            {
                continue;
            }

            var name = row[0].GetString();
            var chinese = row[1].GetString();
            if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(chinese))
            {
                continue;
            }

            var category = row[2].TryGetInt32(out var c) ? c : 0;
            var postCount = row[3].TryGetInt64(out var count) ? count : 0;
            table[name] = new TagLocalizationEntry(chinese, category, postCount);
        }

        return table;
    }
}

/// <summary>内嵌翻译表的条目。</summary>
public readonly record struct TagLocalizationEntry(string ChineseName, int Category, long PostCount);

/// <summary>本地前缀联想的一条结果。</summary>
public sealed record TagLocalizationMatch(string Name, TagLocalizationEntry Entry);
