using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using AnimeDownloader.App.Controls;
using AnimeDownloader.App.Design;
using AnimeDownloader.Download.Contracts;

namespace AnimeDownloader.App.Views;

/// <summary>
/// 下载管理页：展示当前批量下载的任务列表与总体进度。
/// </summary>
internal sealed class DownloadsView : LinearLayout
{
    private readonly Dk _dk;
    private readonly AppServices _services;

    private readonly TextView _eyebrow;
    private readonly TextView _title;
    private readonly TextView _status;
    private readonly PrimaryButtonView? _cancelButton;
    private readonly LinearLayout _list;
    private readonly ScrollView _scroll;

    private DownloadBatchSnapshot _snapshot = DownloadBatchSnapshot.Idle;

    internal DownloadsView(Context context, Dk dk, AppServices services)
        : base(context)
    {
        _dk = dk;
        _services = services;
        Orientation = Android.Widget.Orientation.Vertical;
        SetPadding(dk.Dpi(16f), 0, dk.Dpi(16f), dk.Dpi(16f));

        _eyebrow = new TextView(context)
        {
            Text = "AnimeDownloader",
        };
        _eyebrow.SetTextSize(Android.Util.ComplexUnitType.Sp, dk.TypographyFor.Caption);
        _eyebrow.SetTextColor(dk.TextTertiary);
        AddView(_eyebrow, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(16f),
        });

        _title = new TextView(context)
        {
            Text = "下载任务",
        };
        _title.SetTextSize(Android.Util.ComplexUnitType.Sp, 28f);
        _title.SetTypeface(dk.FontDisplay, TypefaceStyle.Bold);
        _title.SetTextColor(dk.TextPrimary);
        AddView(_title, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(4f),
        });

        _status = new TextView(context)
        {
            Text = "没有进行中的下载",
        };
        _status.SetTextSize(Android.Util.ComplexUnitType.Sp, dk.TypographyFor.Body);
        _status.SetTextColor(dk.TextSecondary);
        _status.SetTypeface(dk.FontMono, TypefaceStyle.Normal);
        AddView(_status, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(8f),
        });

        var cancelRow = new LinearLayout(context)
        {
            Orientation = Android.Widget.Orientation.Horizontal,
        };
        cancelRow.SetGravity(GravityFlags.CenterVertical);
        AddView(cancelRow, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent)
        {
            TopMargin = dk.Dpi(16f),
        });

        _cancelButton = new PrimaryButtonView(context, dk, "取消全部", DkIcons.Close);
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        cancelRow.AddView(_cancelButton);

        _scroll = new ScrollView(context);
        AddView(_scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, 0, 1f)
        {
            TopMargin = dk.Dpi(16f),
        });

        _list = new LinearLayout(context)
        {
            Orientation = Android.Widget.Orientation.Vertical,
        };
        _scroll.AddView(_list, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));
    }

    internal event EventHandler? CancelRequested;

    /// <summary>让页面底部避开悬浮的 Tab Bar（外壳测量后回填）。</summary>
    internal void ApplyBottomBarHeight(int px)
    {
        if (px == _bottomBarHeightPx)
        {
            return;
        }

        _bottomBarHeightPx = px;
        SetPadding(_dk.Dpi(16f), 0, _dk.Dpi(16f), px + _dk.Dpi(16f));
    }

    private int _bottomBarHeightPx;

    internal void SetDownloadSnapshot(DownloadBatchSnapshot snapshot)
    {
        _snapshot = snapshot;
        var busy = snapshot.State is not DownloadBatchState.Idle
            and not DownloadBatchState.Completed
            and not DownloadBatchState.Failed
            and not DownloadBatchState.Cancelled;

        _cancelButton!.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;

        if (busy)
        {
            _status.Text = $"{snapshot.Finished}/{snapshot.Total} · {snapshot.Message}";
        }
        else if (snapshot.Total > 0)
        {
            _status.Text = snapshot.State == DownloadBatchState.Cancelled
                ? $"已取消：{snapshot.Completed}/{snapshot.Total}"
                : $"完成：{snapshot.Completed}/{snapshot.Total}";
        }
        else
        {
            _status.Text = "没有进行中的下载";
        }

        RefreshItems();
    }

    private void RefreshItems()
    {
        _list.RemoveAllViews();
        if (_snapshot.Items.Count == 0)
        {
            var empty = new TextView(Context)
            {
                Text = "队列是空的",
            };
            empty.Gravity = GravityFlags.Center;
            empty.SetTextSize(Android.Util.ComplexUnitType.Sp, _dk.TypographyFor.Body);
            empty.SetTextColor(_dk.TextTertiary);
            _list.AddView(empty, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, _dk.Dpi(120f)));
            return;
        }

        for (var i = 0; i < _snapshot.Items.Count; i++)
        {
            var item = _snapshot.Items[i];
            _list.AddView(BuildItemRow(item));
            if (i < _snapshot.Items.Count - 1)
            {
                _list.AddView(new HairlineView(Context!, _dk), new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, _dk.Dpi(1f)));
            }
        }
    }

    private LinearLayout BuildItemRow(DownloadItemSnapshot item)
    {
        var row = new LinearLayout(Context)
        {
            Orientation = Android.Widget.Orientation.Vertical,
        };
        row.SetPadding(0, _dk.Dpi(12f), 0, _dk.Dpi(12f));

        var top = new LinearLayout(Context)
        {
            Orientation = Android.Widget.Orientation.Horizontal,
        };
        top.SetGravity(GravityFlags.CenterVertical);
        row.AddView(top, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, LinearLayout.LayoutParams.WrapContent));

        var name = System.IO.Path.GetFileName(item.Url) ?? item.Url;
        if (name.Length > 32)
        {
            name = $"{name[..29]}…";
        }

        var nameView = new TextView(Context)
        {
            Text = name,
            Ellipsize = Android.Text.TextUtils.TruncateAt.End,
        };
        nameView.SetSingleLine(true);
        nameView.SetTextSize(Android.Util.ComplexUnitType.Sp, _dk.TypographyFor.Body);
        nameView.SetTextColor(_dk.TextPrimary);
        top.AddView(nameView, new LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WrapContent, 1f));

        var stateView = new TextView(Context)
        {
            Text = StateLabel(item.State),
        };
        stateView.SetTextSize(Android.Util.ComplexUnitType.Sp, _dk.TypographyFor.Caption);
        stateView.SetTypeface(_dk.FontMono, TypefaceStyle.Normal);
        stateView.SetTextColor(StateColor(item.State));
        top.AddView(stateView, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.WrapContent, LinearLayout.LayoutParams.WrapContent)
        {
            LeftMargin = _dk.Dpi(8f),
        });

        var fraction = item.Fraction ?? 0d;
        var progress = new ThinProgressBar(Context!, _dk)
        {
            Progress = fraction,
            Visibility = item.State == DownloadItemState.Running ? ViewStates.Visible : ViewStates.Gone,
        };
        row.AddView(progress, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, _dk.Dpi(2f))
        {
            TopMargin = _dk.Dpi(8f),
        });

        return row;
    }

    private static string StateLabel(DownloadItemState state) => state switch
    {
        DownloadItemState.Queued => "等待",
        DownloadItemState.Running => "下载中",
        DownloadItemState.Completed => "完成",
        DownloadItemState.Failed => "失败",
        DownloadItemState.Skipped => "跳过",
        DownloadItemState.Cancelled => "取消",
        _ => "未知",
    };

    private Color StateColor(DownloadItemState state) => state switch
    {
        DownloadItemState.Completed => _dk.Accent,
        DownloadItemState.Failed => _dk.StatusError,
        DownloadItemState.Running => _dk.Accent,
        _ => _dk.TextTertiary,
    };
}
