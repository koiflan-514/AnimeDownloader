using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Sources;

/// <summary>
/// 应用内置的全部图源注册表。UI 通过 <see cref="CreateAll"/> 获取实例，
/// 并通过 <see cref="ApplySettings"/> 把持久化标签写入各实例。
/// </summary>
public static class SourceRegistry
{
    /// <summary>创建所有内置图源实例（共享同一个 HttpClient）。</summary>
    public static IReadOnlyList<IImageSource> CreateAll(HttpClient http)
    {
        return new IImageSource[]
        {
            new NekosMoeSource(http),
            new WaifuImSource(http),
            new DanbooruSource(http),
            new LoliconSource(http),
            new DmoeSource(http),
            new SafebooruSource(http),
            new YandeReSource(http),
        };
    }

    /// <summary>把设置中保存的标签写入各图源实例。</summary>
    public static void ApplySettings(IEnumerable<IImageSource> sources, AppSettings settings)
    {
        foreach (var source in sources)
        {
            if (source.SupportsTags && settings.SourceTags.TryGetValue(source.Id, out var tags))
            {
                source.Tags = tags ?? string.Empty;
            }
        }
    }
}
