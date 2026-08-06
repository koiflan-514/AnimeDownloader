using AnimeDownloader.Core.Services;

namespace AnimeDownloader.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AnimeDownloaderTests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Load_WhenNoFile_ReturnsDefaults()
    {
        var store = new SettingsStore(_tempDir);

        var settings = store.Load();

        Assert.Equal(NsfwModeStorage.BlockNsfw, settings.NsfwMode);
        Assert.False(settings.AutoReloadEnabled);
        Assert.Equal(30, settings.AutoReloadIntervalSeconds);
        Assert.Equal(12, settings.GalleryCount);
        Assert.Equal("random", settings.GallerySubmode);
        Assert.False(File.Exists(store.ConfigFile), "加载默认值时不应落盘");
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var store = new SettingsStore(_tempDir);
        var settings = store.Load();
        settings.NsfwMode = NsfwModeStorage.OnlyNsfw;
        settings.AutoReloadEnabled = true;
        settings.AutoReloadIntervalSeconds = 15;
        settings.GalleryCount = 24;
        settings.SelectedSource = "danbooru";
        settings.SourceTags["danbooru"] = "cat_ears solo";

        store.Save(settings);

        var reloaded = new SettingsStore(_tempDir).Load();
        Assert.Equal(NsfwModeStorage.OnlyNsfw, reloaded.NsfwMode);
        Assert.True(reloaded.AutoReloadEnabled);
        Assert.Equal(15, reloaded.AutoReloadIntervalSeconds);
        Assert.Equal(24, reloaded.GalleryCount);
        Assert.Equal("danbooru", reloaded.SelectedSource);
        Assert.Equal("cat_ears solo", reloaded.SourceTags["danbooru"]);
    }

    [Fact]
    public void Save_CreatesConfigFileWithDirectory()
    {
        var store = new SettingsStore(_tempDir);

        store.Save(store.Load());

        Assert.True(File.Exists(store.ConfigFile));
        Assert.Equal("config.json", Path.GetFileName(store.ConfigFile));
    }

    [Fact]
    public void SourceTags_Helpers_GetAndSet()
    {
        var store = new SettingsStore(_tempDir);
        var settings = store.Load();

        Assert.Equal(string.Empty, SettingsStore.GetSourceTags(settings, "danbooru"));

        store.SetSourceTags(settings, "danbooru", "solo 1girl");

        Assert.Equal("solo 1girl", SettingsStore.GetSourceTags(new SettingsStore(_tempDir).Load(), "danbooru"));
    }

    [Theory]
    [InlineData(NsfwModeStorage.ShowEverything, "Show everything")]
    [InlineData(NsfwModeStorage.OnlyNsfw, "Only NSFW")]
    [InlineData(NsfwModeStorage.BlockNsfw, "Block NSFW")]
    public void NsfwStorage_ToCore_And_Back(NsfwModeStorage storage, string _)
    {
        var core = storage.ToCore();
        Assert.Equal(storage, core.ToStorage());
    }

    [Fact]
    public void Load_WhenConfigCorrupt_ReturnsDefaultsAndBacksUp()
    {
        var store = new SettingsStore(_tempDir);
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(store.ConfigFile, "{ not valid json !!");

        var settings = store.Load();

        Assert.Equal(NsfwModeStorage.BlockNsfw, settings.NsfwMode);
        var backups = Directory.GetFiles(_tempDir, "config.json.corrupt-*");
        Assert.Single(backups);
    }
}
