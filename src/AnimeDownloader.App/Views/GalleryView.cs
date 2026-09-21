using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AnimeDownloader.Download.Contracts;
using AnimeDownloader.App.Controls;
using AnimeDownloader.App.Design;
using AnimeDownloader.App.Platform;
using AnimeDownloader.Core.Models;
using AnimeDownloader.Core.Services;
using AnimeDownloader.Core.Sources;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 画廊主页 —— 移动端重设计版。
/// </summary>
internal sealed class GalleryView : FrameLayout
{
    private const int MaxGalleryCount = 48;

    private readonly Dk _dk;
    private readonly AppServices _services;
    private readonly GalleryAdapter _adapter;

    private readonly LinearLayout _root;
    private PullToRefreshLayout _pull = null!;
    private RecyclerView _grid = null!;
    private GridLayoutManager _layout = null!;
    private EmptyStateView _empty = null!;
    private PrimaryButtonView _fab = null!;

    private TextView _title = null!;
    private LinesIconView _menuButton = null!;
    private LinearLayout _searchRow = null!;
    private EditText _searchBox = null!;
    private LinesIconView _filterButton = null!;
    private HorizontalScrollView _sourceScroll = null!;
    private LinearLayout _sourceStrip = null!;
    private LinearLayout _filterChips = null!;
    private TextView _statusText = null!;
    private ThinProgressBar _batchBar = null!;

    private readonly List<SourcePillView> _sourcePills = [];
    private readonly List<FilterChipView> _filterChipViews = [];

    private int _page = 1;
    private bool _pagedMode;
    private bool _loading;
    private bool _pendingReload;
    private int _reloadToken;

    internal GalleryView(Context context, Dk dk, AppServices services)
        : base(context)
    {
        _dk = dk;
        _services = services;

        _root = new LinearLayout(context)
        {
            Orientation = Orientation.Vertical,
        };
        AddView(_root, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.MatchParent));

        _adapter = new GalleryAdapter(context, dk, services.Thumbnails);
        _adapter.OpenRequested += (_, item) => RaiseOpen(item);
        _adapter.SaveRequested += (_, item) => SaveItemRequested?.Invoke(this, item);
        _adapter.OpenSourceRequested += (_, item) => OpenSourceRequested?.Invoke(this, item);
        _adapter.CopyUrlRequested += (_, item) => CopyUrl(item);

        BuildHeader(context);
        BuildSearchRow(context);
        BuildSourcePills(context);
        BuildFilterChips(context);
        BuildGrid(context);
        BuildFab(context);

        RestoreFromSettings();
    }

    internal event EventHandler<(IReadOnlyList<ImageItem> Items, int Index)>? OpenLightboxRequested;
    internal event EventHandler<ImageItem>? SaveItemRequested;
    internal event EventHandler<ImageItem>? OpenSourceRequested;
    internal event EventHandler? DownloadAllRequested;
    
    internal IReadOnlyList<ImageItem> Items => _adapter.Items;

    internal void SearchTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var existing = (_searchBox.Text ?? string.Empty).Trim();
        var merged = string.IsNullOrEmpty(existing) ? tag : $"{existing} {tag}";
        _searchBox.Text = merged;
        ApplyTags(merged);
    }

    internal void OnSourceChanged()
    {
        _page = 1;
        RefreshSourcePills();
        UpdateSearchControls();
        UpdatePagingControls();
        Reload();
    }

    internal void Reload() => _ = ReloadAsync();

    internal void ApplyMetrics(int availableWidthPx, ScreenProfile profile)
    {
        // 设计稿固定 2 列：卡片 158×196、栏距 12、页边距 16（360dp 基准）。
        const int columns = 2;
        const float gutterDp = 12f;
        const float marginDp = 16f;
        const float cardPaddingDp = ImageCardView.CardPaddingDp;
        // 卡片高度 = 图片高度 + 54（8 上边距 + 8 下边距 + 元数据行 + 底部留白），
        // 使 158 宽时正好得到设计稿的 196 高。
        const float cardExtraDp = 54f;

        var density = _dk.Dp(1f);
        var availableDp = availableWidthPx / density;
        var totalGutterDp = (columns - 1) * gutterDp + marginDp * 2;
        var cardWidthDp = Math.Max(140f, (availableDp - totalGutterDp) / columns);
        var imageHeightDp = Math.Max(120f, cardWidthDp - cardPaddingDp * 2);
        var cardHeightDp = imageHeightDp + cardExtraDp;

        var cardWidthPx = (int)(cardWidthDp * density);
        var cardHeightPx = (int)(cardHeightDp * density);
        var imageHeightPx = (int)(imageHeightDp * density);
        var tileWidthPx = (int)((cardWidthDp + gutterDp) * density);
        var tileHeightPx = (int)((cardHeightDp + gutterDp) * density);
        var decodePx = imageHeightPx;

        if (_layout.SpanCount != columns)
        {
            _layout.SpanCount = columns;
        }

        // 瓦片宽度含栏距，所以左右各让出「页边距 − 栏距/2」，卡片才会正好落在 16dp 边距上。
        var sidePad = (int)((marginDp - gutterDp / 2f) * density);
        if (_grid.PaddingLeft != sidePad)
        {
            _grid.SetPadding(sidePad, 0, sidePad, (int)(16f * density));
            _grid.SetClipToPadding(false);
        }

        _adapter.SetTileSize(tileWidthPx, tileHeightPx, cardWidthPx, cardHeightPx, imageHeightPx, decodePx);
    }

    /// <summary>底部悬浮 Tab Bar 的总高度（px）：内容让出这块空间，FAB 也据此上移。</summary>
    internal void ApplyBottomBarHeight(int px)
    {
        // 整棵内容树让出底部导航的高度 —— 这样状态行会落在导航之上，
        // 而不是被浮动的胶囊压在下面（设计稿里没有状态行，但它不能因此变得不可读）。
        _root.SetPadding(0, 0, 0, px);

        var sidePad = _grid.PaddingLeft;
        _grid.SetPadding(sidePad, 0, sidePad, _dk.Dpi(16f));
        _grid.SetClipToPadding(false);

        if (_fab.LayoutParameters is FrameLayout.LayoutParams parameters)
        {
            parameters.BottomMargin = px + _dk.Dpi(12f);
            _fab.LayoutParameters = parameters;
        }
    }

    internal void ShowStatus(string text, bool error = false)
    {
        _statusText.Text = text;
        _statusText.SetTextColor(error ? _dk.StatusError : _dk.TextSecondary);
    }

    internal void SetDownloadSnapshot(DownloadBatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var busy = snapshot.State is not DownloadBatchState.Idle
            and not DownloadBatchState.Completed
            and not DownloadBatchState.Failed
            and not DownloadBatchState.Cancelled;

        _batchBar.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        _batchBar.Progress = snapshot.Fraction;
        _fab.Visibility = busy ? ViewStates.Gone : ViewStates.Visible;

        if (busy)
        {
            _statusText.Text = snapshot.Message ?? $"下载中 {snapshot.Finished}/{snapshot.Total}";
        }
    }

    // ---------------- 构建 ----------------

    private void BuildHeader(Context context)
    {
        var header = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
        };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(_dk.Dpi(16f), 0, _dk.Dpi(16f), 0);
        _root.AddView(header, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));

        var left = new LinearLayout(context)
        {
            Orientation = Orientation.Vertical,
        };
        left.SetGravity(GravityFlags.CenterVertical);
        header.AddView(left, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f));

        var eyebrow = new TextView(context)
        {
            Text = "AnimeDownloader",
        };
        eyebrow.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
        eyebrow.SetTypeface(_dk.FontDisplay, TypefaceStyle.Normal);
        eyebrow.SetTextColor(_dk.TextTertiary);
        left.AddView(eyebrow);

        _title = new TextView(context)
        {
            Text = "灵感画廊",
        };
        _title.SetTextSize(Android.Util.ComplexUnitType.Sp, 28f);
        _title.SetTypeface(_dk.FontDisplay, TypefaceStyle.Bold);
        _title.SetTextColor(_dk.TextPrimary);
        left.AddView(_title, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = _dk.Dpi(2f),
        });

        _menuButton = LinesIconView.Menu(context, _dk);
        _menuButton.Click += (_, _) => ShowStatus("菜单待实现");
        header.AddView(_menuButton, new LinearLayout.LayoutParams(_dk.Dpi(40f), _dk.Dpi(40f))
        {
            LeftMargin = _dk.Dpi(8f),
        });
    }

    private void BuildSearchRow(Context context)
    {
        var row = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(_dk.Dpi(16f), _dk.Dpi(12f), _dk.Dpi(16f), 0);
        _root.AddView(row, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));
        _searchRow = row;

        // 搜索框：44dp 高、bg-surface 底、左右各 16dp 内边距，图标与输入之间 8dp。
        var searchContainer = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
            LayoutParameters = new LinearLayout.LayoutParams(0, _dk.Dpi(44f), 1f),
        };
        searchContainer.SetGravity(GravityFlags.CenterVertical);
        row.AddView(searchContainer);

        var searchBg = new GradientDrawable();
        searchBg.SetShape(ShapeType.Rectangle);
        searchBg.SetCornerRadius(_dk.Dp(999f));
        searchBg.SetColor(_dk.CardFill);
        searchContainer.Background = searchBg;
        searchContainer.SetPadding(_dk.Dpi(16f), 0, _dk.Dpi(16f), 0);

        var searchIcon = new TextView(context)
        {
            Text = DkIcons.Search,
            Gravity = GravityFlags.Center,
        };
        searchIcon.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        searchIcon.SetTextColor(_dk.TextTertiary);
        searchContainer.AddView(searchIcon, new LinearLayout.LayoutParams(_dk.Dpi(16f), _dk.Dpi(16f)));

        _searchBox = new EditText(context)
        {
            Hint = "标签搜索（空格分隔）",
        };
        _searchBox.SetSingleLine(true);
        _searchBox.SetTextSize(Android.Util.ComplexUnitType.Sp, 15f);
        _searchBox.SetTextColor(_dk.TextPrimary);
        _searchBox.SetHintTextColor(_dk.TextTertiary);
        _searchBox.SetTypeface(_dk.FontText, TypefaceStyle.Normal);
        _searchBox.Background = null;
        _searchBox.SetPadding(_dk.Dpi(8f), 0, 0, 0);
        _searchBox.ImeOptions = Android.Views.InputMethods.ImeAction.Search;
        _searchBox.EditorAction += (_, args) =>
        {
            if (args.ActionId == Android.Views.InputMethods.ImeAction.Search)
            {
                ApplyTags(_searchBox.Text ?? string.Empty);
            }
        };
        searchContainer.AddView(_searchBox, new LinearLayout.LayoutParams(
            0,
            LinearLayout.LayoutParams.WrapContent,
            1f));

        // 筛选按钮：44dp、bg-surface 底、三线滑杆图标（系统字体没有语义正确的字形，自己画）。
        _filterButton = LinesIconView.Sliders(context, _dk);
        _filterButton.Click += (_, _) => ShowStatus("高级筛选待实现");
        row.AddView(_filterButton, new LinearLayout.LayoutParams(_dk.Dpi(44f), _dk.Dpi(44f))
        {
            LeftMargin = _dk.Dpi(12f),
        });
    }

    private void BuildSourcePills(Context context)
    {
        _sourceScroll = new HorizontalScrollView(context)
        {
            HorizontalScrollBarEnabled = false,
        };
        _sourceScroll.SetPadding(_dk.Dpi(16f), _dk.Dpi(12f), _dk.Dpi(16f), 0);
        _root.AddView(_sourceScroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));

        _sourceStrip = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
        };
        _sourceStrip.SetGravity(GravityFlags.CenterVertical);
        _sourceScroll.AddView(_sourceStrip, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent));

        foreach (var source in _services.Sources)
        {
            var captured = source;
            var pill = new SourcePillView(context, _dk, source.DisplayName);
            pill.Click += (_, _) => SelectSource(captured);
            _sourceStrip.AddView(pill, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
            {
                RightMargin = _dk.Dpi(8f),
            });
            _sourcePills.Add(pill);
        }
    }

    private void BuildFilterChips(Context context)
    {
        _filterChips = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
        };
        _filterChips.SetGravity(GravityFlags.CenterVertical);
        _filterChips.SetPadding(_dk.Dpi(16f), _dk.Dpi(12f), _dk.Dpi(16f), 0);
        _root.AddView(_filterChips, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));

        var chips = new[] { "屏蔽", "随机", "分页", "NSFW" };
        foreach (var label in chips)
        {
            var captured = label;
            var chip = new FilterChipView(context, _dk, label);
            chip.Click += (_, _) => OnFilterChipClick(captured);
            _filterChips.AddView(chip, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
            {
                RightMargin = _dk.Dpi(8f),
            });
            _filterChipViews.Add(chip);
        }
    }

    private void BuildGrid(Context context)
    {
        var gridHost = new FrameLayout(context);
        _grid = new RecyclerView(context);
        _layout = new GridLayoutManager(context, 2);
        _grid.SetLayoutManager(_layout);
        _grid.HasFixedSize = true;
        _grid.SetItemViewCacheSize(8);
        _grid.SetAdapter(_adapter);
        gridHost.AddView(_grid, new FrameLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            LinearLayout.LayoutParams.MatchParent));

        _empty = new EmptyStateView(context, _dk);
        _empty.RetryRequested += (_, _) => Reload();
        gridHost.AddView(_empty, new FrameLayout.LayoutParams(
            LinearLayout.LayoutParams.MatchParent,
            LinearLayout.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Center,
        });

        var statusRow = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
        };
        statusRow.SetGravity(GravityFlags.CenterVertical);
        statusRow.SetPadding(_dk.Dpi(16f), _dk.Dpi(8f), _dk.Dpi(16f), _dk.Dpi(8f));

        _statusText = new TextView(context)
        {
            Text = "就绪",
            Ellipsize = TextUtils.TruncateAt.End,
        };
        _statusText.SetSingleLine(true);
        _statusText.SetTextSize(Android.Util.ComplexUnitType.Sp, _dk.TypographyFor.Caption);
        _statusText.SetTypeface(_dk.FontMono, TypefaceStyle.Normal);
        _statusText.SetTextColor(_dk.TextSecondary);
        statusRow.AddView(_statusText, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f));

        _batchBar = new ThinProgressBar(context, _dk) { Visibility = ViewStates.Gone };
        statusRow.AddView(_batchBar, new LinearLayout.LayoutParams(_dk.Dpi(80f), _dk.Dpi(2f))
        {
            Gravity = GravityFlags.CenterVertical,
        });

        var gridWithStatus = new LinearLayout(context)
        {
            Orientation = Orientation.Vertical,
        };
        gridWithStatus.AddView(gridHost, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, 0, 1f));
        gridWithStatus.AddView(statusRow, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));

        _pull = new PullToRefreshLayout(context, _dk);
        _pull.SetContent(gridWithStatus);
        _pull.RefreshRequested += (_, _) => Reload();
        _root.AddView(_pull, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, 0, 1f)
        {
            TopMargin = _dk.Dpi(12f),
        });
    }

    private void BuildFab(Context context)
    {
        _fab = new PrimaryButtonView(context, _dk, "下载全部", DkIcons.ZoomIn);
        _fab.Click += (_, _) =>
        {
            if (_fab.Visibility == ViewStates.Visible)
            {
                DownloadAllRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        AddView(_fab, new FrameLayout.LayoutParams(FrameLayout.LayoutParams.WrapContent, FrameLayout.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom | GravityFlags.Right,
            RightMargin = _dk.Dpi(16f),
            BottomMargin = _dk.Dpi(88f),
        });
    }

    // ---------------- 逻辑 ----------------

    private void RestoreFromSettings()
    {
        _pagedMode = _services.Settings.GallerySubmode == "paged";
        _searchBox.Text = _services.CurrentSource.Tags ?? string.Empty;
        RefreshSourcePills();
        RefreshFilterChips();
        UpdateSearchControls();
        UpdatePagingControls();
    }

    private void RefreshSourcePills()
    {
        var selectedIndex = -1;
        for (var i = 0; i < _services.Sources.Count && i < _sourcePills.Count; i++)
        {
            var isCurrent = _services.Sources[i].Id == _services.CurrentSource.Id;
            _sourcePills[i].Selected = isCurrent;
            if (isCurrent)
            {
                selectedIndex = i;
            }
        }

        _title.Text = "灵感画廊";

        // 图源较多时选中的 Pill 会被挤出可视区，用户就看不到「当前是哪个源」——滚到它。
        if (selectedIndex >= 0 && selectedIndex < _sourcePills.Count)
        {
            var pill = _sourcePills[selectedIndex];
            _sourceScroll.Post(() =>
            {
                var target = Math.Max(0, pill.Left - (int)_dk.Dp(16f));
                _sourceScroll.SmoothScrollTo(target, 0);
            });
        }
    }

    private void RefreshFilterChips()
    {
        var blockActive = _services.Settings.NsfwMode == NsfwModeStorage.BlockNsfw;
        var nsfwActive = _services.Settings.NsfwMode == NsfwModeStorage.OnlyNsfw;

        foreach (var chip in _filterChipViews)
        {
            chip.Selected = chip.Text switch
            {
                "屏蔽" => blockActive,
                "NSFW" => nsfwActive,
                "随机" => !_pagedMode,
                "分页" => _pagedMode,
                _ => false,
            };
        }
    }

    private void OnFilterChipClick(string label)
    {
        switch (label)
        {
            case "屏蔽":
                _services.Settings.NsfwMode = NsfwModeStorage.BlockNsfw;
                break;
            case "NSFW":
                _services.Settings.NsfwMode = NsfwModeStorage.OnlyNsfw;
                break;
            case "随机":
                _pagedMode = false;
                _services.Settings.GallerySubmode = "random";
                break;
            case "分页":
                _pagedMode = true;
                _services.Settings.GallerySubmode = "paged";
                break;
        }

        _services.SaveSettings();
        _page = 1;
        RefreshFilterChips();
        UpdatePagingControls();
        Reload();
    }

    private void SelectSource(IImageSource source)
    {
        if (_services.CurrentSource.Id == source.Id)
        {
            return;
        }

        _services.CurrentSourceId = source.Id;
        _services.SaveSettings();
        OnSourceChanged();
    }

    private void UpdateSearchControls()
    {
        var supportsTags = _services.CurrentSource.SupportsTags;
        // 整行一起收：只藏输入框与按钮会留下一个空胶囊，看着像坏了。
        _searchRow.Visibility = supportsTags ? ViewStates.Visible : ViewStates.Gone;
        _searchBox.Visibility = supportsTags ? ViewStates.Visible : ViewStates.Gone;
        _filterButton.Visibility = supportsTags ? ViewStates.Visible : ViewStates.Gone;
        if (!supportsTags)
        {
            return;
        }

        var tags = _services.CurrentSource.Tags ?? string.Empty;
        if (_searchBox.Text != tags)
        {
            _searchBox.Text = tags;
        }
    }

    private static void UpdatePagingControls()
    {
        // 随机模式下不显示分页；分页模式下在内部仍用 _page。
    }

    private void ApplyTags(string tags)
    {
        var source = _services.CurrentSource;
        if (!source.SupportsTags)
        {
            return;
        }

        var trimmed = tags.Trim();
        source.Tags = trimmed;
        _services.Settings.SourceTags[source.Id] = trimmed;
        _services.SaveSettings();
        _page = 1;
        UpdateSearchControls();
        Reload();
    }

    private async Task ReloadAsync()
    {
        if (_loading)
        {
            _pendingReload = true;
            return;
        }

        _loading = true;
        var token = ++_reloadToken;
        var source = _services.CurrentSource;
        _statusText.Text = "加载中…";
        _empty.Hide();

        try
        {
            var count = Math.Clamp(
                _services.Settings.GalleryCount > 0 ? _services.Settings.GalleryCount : 12,
                1,
                MaxGalleryCount);
            var items = _pagedMode && source.SupportsPaging
                ? await source.GetImagesPageAsync(_services.CurrentNsfwMode, _page, count)
                : await source.GetImagesAsync(_services.CurrentNsfwMode, count);

            if (token != _reloadToken)
            {
                return;
            }

            _adapter.SetItems(items, _services.Settings.ThumbnailProxyTemplate);
            if (items.Count == 0)
            {
                var message = string.IsNullOrEmpty(source.LastError)
                    ? "没有找到图片。可尝试更换图源、切换 NSFW 模式或调整标签。"
                    : $"加载失败：{source.LastError}";
                _empty.Show("没有找到图片", message);
                ShowStatus(message, error: !string.IsNullOrEmpty(source.LastError));
            }
            else
            {
                ShowStatus($"{source.DisplayName} · 共 {items.Count} 张图片");
            }
        }
        catch (Exception ex)
        {
            if (token != _reloadToken)
            {
                return;
            }

            _empty.Show("加载失败", ex.Message);
            ShowStatus($"加载失败：{ex.Message}", error: true);
        }
        finally
        {
            _loading = false;
            _pull.EndRefresh();
            if (_pendingReload)
            {
                _pendingReload = false;
                Reload();
            }
        }
    }

    private void CopyUrl(ImageItem item)
    {
        var clipboard = Context!.GetSystemService(Context.ClipboardService) as Android.Content.ClipboardManager;
        clipboard?.PrimaryClip = ClipData.NewPlainText("图片地址", item.Url);
        ShowStatus("已复制图片地址");
    }

    private void RaiseOpen(ImageItem item)
    {
        var items = _adapter.Items;
        var index = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
            {
                index = i;
                break;
            }
        }

        OpenLightboxRequested?.Invoke(this, (items, index));
    }
}
