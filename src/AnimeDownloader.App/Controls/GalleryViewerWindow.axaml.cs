using AnimeDownloader.App.Platform;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// Full-window viewer for a gallery item list: previous/next with keyboard support, background
/// preload of the next image, save with progress and fullscreen toggle.
/// </summary>
public sealed partial class GalleryViewerWindow : Window
{
    private readonly IReadOnlyList<ImageItem> _items;
    private readonly MainWindow _owner;
    private readonly ImageDownloader _downloader;
    private int _index;
    private int _loadToken;
    private byte[]? _content;
    private string? _nextUrl;
    private byte[]? _nextContent;
    private bool _isSaving;
    private bool _hasTags;
    private int _windowThumbToken;

    public GalleryViewerWindow(MainWindow owner, IReadOnlyList<ImageItem> items, int index)
    {
        InitializeComponent();
        _owner = owner;
        _items = items;
        _index = index;
        _downloader = owner.Downloader;

        RequestedThemeVariant = App.ResolveTheme(App.Settings.Theme);
        // 图标 + 标题栏着色；不设首屏尺寸（构造前已在 XAML 里给 960×680），
        // 也不设最小尺寸 —— 与 WinUI 版一致（那个窗口是可以缩到很小的）。
        WindowChrome.Apply(this, withInitialBounds: false);
        Opened += (_, _) => WindowChrome.ApplyTitleBar(this);
        KeyDown += OnWindowKeyDown;

        ShowItem();
        if (NextButton.IsEnabled)
        {
            NextButton.Focus();
        }
        else if (PrevButton.IsEnabled)
        {
            PrevButton.Focus();
        }
        else
        {
            SaveButton.Focus();
        }
    }

    private void ShowItem()
    {
        if (_items.Count == 0)
        {
            IndexLabel.Text = "0 / 0";
            return;
        }

        IndexLabel.Text = $"{_index + 1} / {_items.Count}";
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;
        _owner.SetHeaderThumbnail(_items[_index]);
        PopulateTags(_items[_index]);
        _ = LoadWindowThumbAsync(_items[_index]);
        _ = LoadImageAsync(_items[_index].Url);
        _ = PreloadNextAsync();
    }

    /// <summary>填充当前图片的全部标签 chip；点击跳转到该标签的画廊视图。</summary>
    private void PopulateTags(ImageItem item)
    {
        TagsList.Children.Clear();
        var tags = item.TagList;
        _hasTags = tags.Count > 0;
        TagsHost.IsVisible = _hasTags;
        if (tags.Count == 0)
        {
            return;
        }

        foreach (var tag in tags)
        {
            var hasChinese = TagLocalization.TryGet(tag.Name, out var localized);
            var chip = new Button
            {
                Theme = AppTheme.Lookup("AppTagChipButton"),
                Margin = new Thickness(0, 0, 6, 6),
            };
            var panel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6,
            };
            if (tag.Category is { } category)
            {
                panel.Children.Add(new Avalonia.Controls.Shapes.Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(CategoryColor(category)),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
            }

            var parts = new List<string>();
            if (hasChinese)
            {
                parts.Add(localized.ChineseName);
            }

            parts.Add(tag.Name);
            panel.Children.Add(new TextBlock { Text = string.Join(' ', parts) });
            chip.Content = panel;

            var tooltipLines = new List<string>();
            if (hasChinese)
            {
                tooltipLines.Add($"中文：{localized.ChineseName}");
            }

            if (tag.Category is { } cat)
            {
                tooltipLines.Add($"类别：{cat}");
            }

            ToolTip.SetTip(chip, string.Join('\n', tooltipLines));
            var tagName = tag.Name;
            chip.Click += (_, _) => CloseAndNavigate(tagName);
            TagsList.Children.Add(chip);
        }
    }

    /// <summary>关闭查看器窗口并让主窗口跳转到该标签视图。</summary>
    private void CloseAndNavigate(string tagName)
    {
        Close();
        _owner.NavigateToTagView(tagName);
    }

    private static Color CategoryColor(TagCategory category) => category switch
    {
        TagCategory.Artist => Color.FromArgb(255, 0xE8, 0xA2, 0x3D),
        TagCategory.Character => Color.FromArgb(255, 0x35, 0xC0, 0x75),
        TagCategory.Copyright => Color.FromArgb(255, 0x9B, 0x59, 0xD0),
        TagCategory.Meta => Color.FromArgb(255, 0xE5, 0x48, 0x4D),
        TagCategory.Circle => Color.FromArgb(255, 0x4C, 0xA6, 0xC9),
        _ => Color.FromArgb(255, 0x80, 0x80, 0x80),
    };

    private async Task LoadWindowThumbAsync(ImageItem item)
    {
        var token = ++_windowThumbToken;
        var thumbUrl = ThumbnailResolver.ResolveThumbnailUrl(
            item.ThumbnailUrl,
            item.Url,
            _owner.Settings.ThumbnailProxyTemplate);
        if (thumbUrl is null)
        {
            WindowThumb.Source = null;
            WindowThumbBorder.IsVisible = false;
            return;
        }

        try
        {
            var bytes = await _downloader.DownloadCachedAsync(thumbUrl);
            if (token != _windowThumbToken)
            {
                return;
            }

            // WinUI 是 DecodePixelWidth=80 / DecodePixelType=Logical；
            // Avalonia 对应 Bitmap.DecodeToWidth（按像素宽有界解码）。
            using var stream = new MemoryStream(bytes);
            WindowThumb.Source = Bitmap.DecodeToWidth(stream, 80, BitmapInterpolationMode.MediumQuality);
            WindowThumbBorder.IsVisible = true;
        }
        catch (Exception)
        {
            if (token == _windowThumbToken)
            {
                WindowThumb.Source = null;
                WindowThumbBorder.IsVisible = false;
            }
        }
    }

    private async Task LoadImageAsync(string url)
    {
        var token = ++_loadToken;
        LoadingRing.IsActive = true;
        ErrorPanel.IsVisible = false;
        ViewImage.Source = null;
        _content = null;
        try
        {
            byte[] bytes;
            if (_nextUrl == url && _nextContent is not null)
            {
                bytes = _nextContent;
                _nextUrl = null;
                _nextContent = null;
            }
            else
            {
                bytes = await _downloader.DownloadAsync(url);
            }

            if (token != _loadToken)
            {
                return;
            }

            _content = bytes;
            using var stream = new MemoryStream(bytes);
            ViewImage.Source = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            if (token != _loadToken)
            {
                return;
            }

            ErrorText.Text = $"加载失败：{ex.Message}";
            ErrorPanel.IsVisible = true;
        }
        finally
        {
            if (token == _loadToken)
            {
                LoadingRing.IsActive = false;
            }
        }
    }

    private async Task PreloadNextAsync()
    {
        if (_index >= _items.Count - 1)
        {
            return;
        }

        var nextItem = _items[_index + 1];
        try
        {
            _nextContent = await _downloader.DownloadAsync(nextItem.Url);
            _nextUrl = nextItem.Url;
        }
        catch (Exception)
        {
            // Preload is best-effort; navigation will download on demand.
        }
    }

    private void OnPrev(object? sender, RoutedEventArgs e)
    {
        if (_index > 0)
        {
            _index--;
            ShowItem();
        }
    }

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_index < _items.Count - 1)
        {
            _index++;
            ShowItem();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                e.Handled = true;
                OnPrev(sender, new RoutedEventArgs());
                break;
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                e.Handled = true;
                OnNext(sender, new RoutedEventArgs());
                break;
            case Key.F11:
                e.Handled = true;
                OnToggleFullscreen(sender, new RoutedEventArgs());
                break;
            case Key.Escape:
                if (IsFullscreen())
                {
                    e.Handled = true;
                    OnToggleFullscreen(sender, new RoutedEventArgs());
                }

                break;
        }
    }

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e)
    {
        // 走公共实现：退出全屏时会还原窗口状态（原本最大化的要回到最大化）。
        // Avalonia 的 WindowState.FullScreen 不换窗口对象，MinMax 约束也一直有效。
        WindowChrome.SetFullscreen(this, !IsFullscreen());
        var fullscreen = IsFullscreen();
        Toolbar.IsVisible = !fullscreen;
        TagsHost.IsVisible = !fullscreen && _hasTags;
    }

    private bool IsFullscreen() => WindowChrome.IsFullscreen(this);

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_index < 0 || _index >= _items.Count || _isSaving)
        {
            return;
        }

        _isSaving = true;
        SaveButton.IsEnabled = false;
        try
        {
            var item = _items[_index];
            if (_content is null)
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    var percent = p.Percent is { } value ? (int)(value * 100) : 0;
                    IndexLabel.Text = percent > 0 ? $"保存中 {percent}%" : "保存中…";
                });
                _content = await _downloader.DownloadAsync(item.Url, progress);
            }

            var extension = item.Extension ?? "png";
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "保存图片",
                SuggestedFileName = item.SuggestFileName(),
                DefaultExtension = extension,
                ShowOverwritePrompt = true,
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("图片") { Patterns = new[] { $"*.{extension}" } },
                },
            });
            if (file is null)
            {
                return;
            }

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(_content);
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"保存失败：{ex.Message}";
            ErrorPanel.IsVisible = true;
        }
        finally
        {
            IndexLabel.Text = _items.Count == 0 ? "0 / 0" : $"{_index + 1} / {_items.Count}";
            SaveButton.IsEnabled = true;
            _isSaving = false;
        }
    }
}
