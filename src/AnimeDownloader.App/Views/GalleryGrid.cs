using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AnimeDownloader.App.Design;
using AnimeDownloader.App.Thumbnails;
using AnimeDownloader.Core.Models;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 画廊卡片 —— 重设计版：158×196 圆角卡片，8dp 内边距，140×140 方图，底部作者 + 分辨率。
/// </summary>
internal sealed class ImageCardView : LinearLayout
{
    private const int ShimmerPeriodMs = 1400;
    internal const int EntranceStaggerMs = 28;

    private readonly Dk _dk;
    private readonly ThumbnailLoader _loader;
    private readonly FrameLayout _imageHost;
    private readonly LinearLayout.LayoutParams _imageParams;
    private readonly ImageView _image;
    private readonly ShimmerView _shimmer;
    private readonly TextView _artist;
    private readonly TextView _resolution;

    private CancellationTokenSource? _loadCts;
    private ImageItem? _item;
    private bool _bound;

    internal ImageCardView(Context context, Dk dk, ThumbnailLoader loader)
        : base(context)
    {
        _dk = dk;
        _loader = loader;
        Orientation = Orientation.Vertical;
        Elevation = 0f;
        Clickable = true;
        Focusable = true;
        SetPadding(dk.Dpi(CardPaddingDp), dk.Dpi(CardPaddingDp), dk.Dpi(CardPaddingDp), dk.Dpi(CardPaddingDp));

        var corner = dk.Dp(12f);
        var background = new GradientDrawable();
        background.SetShape(ShapeType.Rectangle);
        background.SetCornerRadius(corner);
        background.SetColor(dk.CardFill);
        Background = background;

        // 图片区固定高度（设计稿 140dp），不参与拉伸 —— 卡片多出来的高度留给底部留白，
        // 这样 158×196 的比例在任何屏宽下都成立。
        _imageParams = new LayoutParams(LayoutParams.MatchParent, dk.Dpi(140f));
        _imageHost = new FrameLayout(context)
        {
            LayoutParameters = _imageParams,
        };
        AddView(_imageHost);

        _image = new ImageView(context)
        {
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent),
        };
        _image.SetScaleType(ImageView.ScaleType.CenterCrop);
        _image.OutlineProvider = new RoundedOutline(corner);
        _image.ClipToOutline = true;
        _imageHost.AddView(_image);

        _shimmer = new ShimmerView(context, dk)
        {
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent),
        };
        _imageHost.AddView(_shimmer);

        // 1px 极细描边覆盖层：设计稿的图片占位框有一道 border 线，画在图片之上才不会被盖住。
        var outline = new View(context)
        {
            LayoutParameters = new LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent),
            Clickable = false,
            Focusable = false,
        };
        var outlineShape = new GradientDrawable();
        outlineShape.SetShape(ShapeType.Rectangle);
        outlineShape.SetCornerRadius(corner);
        outlineShape.SetColor(Color.Transparent);
        outlineShape.SetStroke(dk.Dpi(1f), dk.Hairline);
        outline.Background = outlineShape;
        _imageHost.AddView(outline);

        // 图片与元数据之间的弹性留白：让作者/分辨率贴住卡片底边（设计稿的底部留白就是这个）。
        var spacer = new View(context);
        AddView(spacer, new LayoutParams(LayoutParams.MatchParent, 0, 1f));

        var meta = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
        };
        meta.SetGravity(GravityFlags.CenterVertical);
        AddView(meta, new LayoutParams(LayoutParams.MatchParent, LayoutParams.WrapContent));

        _artist = new TextView(context)
        {
            Ellipsize = Android.Text.TextUtils.TruncateAt.End,
        };
        _artist.SetSingleLine(true);
        _artist.SetTextSize(Android.Util.ComplexUnitType.Sp, 12f);
        _artist.SetTypeface(dk.FontDisplay, TypefaceStyle.Normal);
        _artist.SetTextColor(dk.TextPrimary);
        meta.AddView(_artist, new LayoutParams(0, LayoutParams.WrapContent, 1f));

        _resolution = new TextView(context)
        {
            Gravity = GravityFlags.End | GravityFlags.CenterVertical,
        };
        _resolution.SetSingleLine(true);
        _resolution.SetTextSize(Android.Util.ComplexUnitType.Sp, 11f);
        _resolution.SetTypeface(dk.FontText, TypefaceStyle.Normal);
        _resolution.SetTextColor(dk.TextTertiary);
        meta.AddView(_resolution, new LayoutParams(LayoutParams.WrapContent, LayoutParams.WrapContent));

        Click += OnClick;
        LongClick += OnLongClick;
    }

    /// <summary>卡片内边距（dp），设计稿固定 8。</summary>
    internal const float CardPaddingDp = 8f;

    /// <summary>设置图片区高度（px）。卡片宽度由外部布局决定，图片区高度随宽度推导。</summary>
    internal void SetImageHeight(int px)
    {
        if (px <= 0 || _imageParams.Height == px)
        {
            return;
        }

        _imageParams.Height = px;
        _imageHost.LayoutParameters = _imageParams;
    }

    internal event EventHandler<ImageItem>? OpenRequested;
    internal event EventHandler<ImageItem>? SaveRequested;
    internal event EventHandler<ImageItem>? OpenSourceRequested;
    internal event EventHandler<ImageItem>? CopyUrlRequested;
    internal ImageItem? Item => _item;

    internal void Bind(ImageItem item, int imagePx, string? thumbnailProxyTemplate, int entranceDelayMs)
    {
        ArgumentNullException.ThrowIfNull(item);

        Unbind();
        _item = item;
        _bound = true;

        _image.SetImageDrawable(null);
        _artist.Text = item.Artist ?? item.Id ?? string.Empty;
        _resolution.Text = item.Dimensions is { } size ? $"{size.Width}×{size.Height}" : string.Empty;
        _resolution.Visibility = _resolution.Text?.Length > 0 ? ViewStates.Visible : ViewStates.Gone;

        if (entranceDelayMs >= 0)
        {
            Alpha = 0f;
            TranslationY = _dk.Dp(14f);
            Animate()!
                .Alpha(1f)
                .TranslationY(0f)
                .SetStartDelay(entranceDelayMs)
                .SetDuration(220)
                .Start();
        }
        else
        {
            Alpha = 1f;
            TranslationY = 0f;
        }

        _shimmer.Start();
        _loadCts = new CancellationTokenSource();
        _ = LoadAsync(item, ResolveUrl(item, thumbnailProxyTemplate), imagePx, _loadCts.Token);
    }

    internal void Unbind()
    {
        _bound = false;
        _item = null;
        _shimmer.Stop();

        var cts = _loadCts;
        _loadCts = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _image.SetImageDrawable(null);
    }

    private static string ResolveUrl(ImageItem item, string? proxyTemplate)
    {
        if (!string.IsNullOrWhiteSpace(proxyTemplate)
            && proxyTemplate!.Contains("{url}", StringComparison.Ordinal))
        {
            var raw = item.ThumbnailUrl ?? item.Url;
            return proxyTemplate.Replace("{url}", Uri.EscapeDataString(raw), StringComparison.Ordinal);
        }

        return item.ThumbnailUrl ?? item.Url;
    }

    private async Task LoadAsync(ImageItem item, string url, int targetPx, CancellationToken cancellationToken)
    {
        var bitmap = await _loader.LoadAsync(url, targetPx, cancellationToken).ConfigureAwait(false);

        Post(() =>
        {
            if (!_bound || !ReferenceEquals(_item, item) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _shimmer.Stop();
            if (bitmap is null)
            {
                return;
            }

            _image.SetImageBitmap(bitmap);
            _image.Alpha = 0f;
            _image.Animate()!.Alpha(1f).SetDuration(160).Start();
        });
    }

    private void OnClick(object? sender, EventArgs e)
    {
        if (_item is not null)
        {
            OpenRequested?.Invoke(this, _item);
        }
    }

    private void OnLongClick(object? sender, LongClickEventArgs e)
    {
        if (_item is null)
        {
            return;
        }

        var popup = new PopupMenu(Context, this);
        var menu = popup.Menu;
        menu?.Add(0, MenuActionSave, 0, Resource.String.menu_save);
        if (!string.IsNullOrWhiteSpace(_item.SourceLink))
        {
            menu?.Add(0, MenuActionSource, 1, Resource.String.menu_open_source);
        }

        menu?.Add(0, MenuActionCopy, 2, Resource.String.menu_copy_url);

        popup.MenuItemClick += (_, args) =>
        {
            var item = _item;
            if (item is null)
            {
                return;
            }

            switch (args.Item?.ItemId)
            {
                case MenuActionSave:
                    SaveRequested?.Invoke(this, item);
                    break;
                case MenuActionSource:
                    OpenSourceRequested?.Invoke(this, item);
                    break;
                case MenuActionCopy:
                    CopyUrlRequested?.Invoke(this, item);
                    break;
            }
        };
        popup.Show();
    }

    private const int MenuActionSave = 1;
    private const int MenuActionSource = 2;
    private const int MenuActionCopy = 3;

    private sealed class RoundedOutline(float radius) : ViewOutlineProvider
    {
        public override void GetOutline(View? view, Outline? outline)
        {
            if (view is null || outline is null)
            {
                return;
            }

            outline.SetRoundRect(0, 0, view.Width, view.Height, radius);
        }
    }

    private sealed class ShimmerView : View
    {
        private readonly Paint _paint = new() { AntiAlias = true };
        private readonly LinearGradient _gradient;
        private float _phase = -0.4f;
        private bool _running;

        public ShimmerView(Context context, Dk dk)
            : base(context)
        {
            SetWillNotDraw(false);
            _gradient = new LinearGradient(
                0f,
                0f,
                1f,
                0f,
                [Color.Argb(0x00, 0xFF, 0xFF, 0xFF), Color.Argb(0x12, 0xFF, 0xFF, 0xFF), Color.Argb(0x00, 0xFF, 0xFF, 0xFF)],
                [0f, 0.5f, 1f],
                Shader.TileMode.Clamp!);
            _paint.SetShader(_gradient);

            var placeholder = new GradientDrawable();
            placeholder.SetShape(ShapeType.Rectangle);
            placeholder.SetCornerRadius(dk.Dp(12f));
            placeholder.SetColor(dk.Sunken);
            Background = placeholder;
        }

        public void Start()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            Visibility = ViewStates.Visible;
            _phase = -0.4f;
            PostOnAnimation(new Java.Lang.Runnable(Tick));
        }

        public void Stop()
        {
            _running = false;
            Visibility = ViewStates.Invisible;
        }

        protected override void OnDetachedFromWindow()
        {
            _running = false;
            base.OnDetachedFromWindow();
        }

        protected override void OnDraw(Canvas canvas)
        {
            ArgumentNullException.ThrowIfNull(canvas);
            base.OnDraw(canvas);

            if (!_running || Width <= 0)
            {
                return;
            }

            var width = Width * 0.4f;
            var left = _phase * Width;
            canvas.Save();
            canvas.ClipRect(0, 0, Width, Height);
            canvas.Translate(left, 0f);
            canvas.DrawRect(0, 0, width, Height, _paint);
            canvas.Restore();
        }

        private void Tick()
        {
            if (!_running || !IsAttachedToWindow)
            {
                return;
            }

            _phase += 0.02f;
            if (_phase > 1f)
            {
                _phase = -0.4f;
                PostDelayed(Tick, ShimmerPeriodMs / 3);
                return;
            }

            Invalidate();
            PostOnAnimation(new Java.Lang.Runnable(Tick));
        }
    }
}

/// <summary>
/// 画廊网格适配器：2 列瀑布式卡片。
/// </summary>
internal sealed class GalleryAdapter : RecyclerView.Adapter
{
    private readonly Context _context;
    private readonly Dk _dk;
    private readonly ThumbnailLoader _loader;

    private IReadOnlyList<ImageItem> _items = [];
    private readonly List<(ImageItem Item, ImageCardView Card)> _live = [];
    private int _tileWidthPx;
    private int _tileHeightPx;
    private int _cardWidthPx;
    private int _cardHeightPx;
    private int _imageHeightPx;
    private int _imagePx;
    private bool _animateEntrance;
    private string? _thumbnailProxyTemplate;

    internal GalleryAdapter(Context context, Dk dk, ThumbnailLoader loader)
        : base()
    {
        _context = context;
        _dk = dk;
        _loader = loader;
    }

    internal event EventHandler<ImageItem>? OpenRequested;
    internal event EventHandler<ImageItem>? SaveRequested;
    internal event EventHandler<ImageItem>? OpenSourceRequested;
    internal event EventHandler<ImageItem>? CopyUrlRequested;

    internal IReadOnlyList<ImageItem> Items => _items;
    internal int Count => _items.Count;

    internal void SetItems(IReadOnlyList<ImageItem> items, string? thumbnailProxyTemplate)
    {
        foreach (var (_, card) in _live)
        {
            card.Unbind();
        }

        _live.Clear();
        _items = items ?? [];
        _thumbnailProxyTemplate = thumbnailProxyTemplate;
        _animateEntrance = true;
        NotifyDataSetChanged();
    }

    /// <summary>
    /// 设置瓦片与卡片尺寸。
    /// </summary>
    /// <param name="tileWidthPx">单个瓦片宽度（px，含栏距）。</param>
    /// <param name="tileHeightPx">单个瓦片高度（px，含栏距）。</param>
    /// <param name="cardWidthPx">卡片宽度（px）。</param>
    /// <param name="cardHeightPx">卡片高度（px）。</param>
    /// <param name="imageHeightPx">卡片内图片区高度（px）。</param>
    /// <param name="imagePx">图片解码目标尺寸（px）。</param>
    internal void SetTileSize(
        int tileWidthPx,
        int tileHeightPx,
        int cardWidthPx,
        int cardHeightPx,
        int imageHeightPx,
        int imagePx)
    {
        _tileWidthPx = tileWidthPx;
        _tileHeightPx = tileHeightPx;
        _cardWidthPx = cardWidthPx;
        _cardHeightPx = cardHeightPx;
        _imageHeightPx = imageHeightPx;
        _imagePx = imagePx;
        NotifyDataSetChanged();
    }

    public override int ItemCount => _items.Count;

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup? parent, int viewType)
    {
        var tile = new FrameLayout(_context)
        {
            LayoutParameters = new RecyclerView.LayoutParams(_tileWidthPx, _tileHeightPx),
        };
        var card = new ImageCardView(_context, _dk, _loader)
        {
            LayoutParameters = new FrameLayout.LayoutParams(_cardWidthPx, _cardHeightPx)
            {
                Gravity = GravityFlags.Center,
            },
        };
        card.SetImageHeight(_imageHeightPx);
        card.OpenRequested += (_, item) => OpenRequested?.Invoke(this, item);
        card.SaveRequested += (_, item) => SaveRequested?.Invoke(this, item);
        card.OpenSourceRequested += (_, item) => OpenSourceRequested?.Invoke(this, item);
        card.CopyUrlRequested += (_, item) => CopyUrlRequested?.Invoke(this, item);
        tile.AddView(card);

        return new GalleryViewHolder(tile, card);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder? holder, int position)
    {
        if (holder is not GalleryViewHolder vh)
        {
            return;
        }

        if (position < 0 || position >= _items.Count)
        {
            vh.Card.Unbind();
            return;
        }

        if (vh.ItemView.LayoutParameters is RecyclerView.LayoutParams parameters
            && (parameters.Width != _tileWidthPx || parameters.Height != _tileHeightPx))
        {
            parameters.Width = _tileWidthPx;
            parameters.Height = _tileHeightPx;
            vh.ItemView.LayoutParameters = parameters;
        }

        if (vh.Card.LayoutParameters is FrameLayout.LayoutParams cardParameters
            && (cardParameters.Width != _cardWidthPx || cardParameters.Height != _cardHeightPx))
        {
            cardParameters.Width = _cardWidthPx;
            cardParameters.Height = _cardHeightPx;
            vh.Card.LayoutParameters = cardParameters;
        }

        vh.Card.SetImageHeight(_imageHeightPx);

        var delay = _animateEntrance && position < 12
            ? position * ImageCardView.EntranceStaggerMs
            : -1;
        if (_animateEntrance && position == Math.Min(11, _items.Count - 1))
        {
            _animateEntrance = false;
        }

        vh.Card.Bind(_items[position], _imagePx, _thumbnailProxyTemplate, delay);
        Track(vh.Card, _items[position]);
    }

    public override void OnViewRecycled(Java.Lang.Object? holder)
    {
        if (holder is GalleryViewHolder vh)
        {
            vh.Card.Unbind();
            Untrack(vh.Card);
        }

        if (holder is not null)
        {
            base.OnViewRecycled(holder);
        }
    }

    private void Track(ImageCardView card, ImageItem item)
    {
        Untrack(card);
        _live.Add((item, card));
    }

    private void Untrack(ImageCardView card)
    {
        var index = _live.FindIndex(entry => ReferenceEquals(entry.Card, card));
        if (index >= 0)
        {
            _live.RemoveAt(index);
        }
    }

    private sealed class GalleryViewHolder(View itemView, ImageCardView card)
        : RecyclerView.ViewHolder(itemView)
    {
        public ImageCardView Card { get; } = card;
    }
}
