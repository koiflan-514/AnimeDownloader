using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 确认对话框。Avalonia 没有 <c>ContentDialog</c>，这里按原版用到的能力重建：
/// 标题 / 正文 / 主按钮 / 关闭按钮 / 返回用户的选择。
///
/// 为什么不用独立窗口 <c>ShowDialog</c>：原版是**应用内**的内容对话框
/// （一块烟雾遮罩上浮起的面板，窗口标题栏不动），换成独立窗口会多出一套系统装饰，
/// 视觉与交互都变了。这里把对话框作为根 Grid 的最后一个子元素铺满整窗，
/// 用 <c>AppSmokeBrush</c> 遮罩压暗背后，与原版一致。
///
/// 遮罩自身带背景（不是透明），因此天然吃掉下层控件的点击 —— 不需要额外拦截。
/// </summary>
public sealed class ConfirmDialog : ContentControl
{
    /// <summary>标题。</summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Title));

    /// <summary>主按钮文案。</summary>
    public static readonly StyledProperty<string?> PrimaryButtonTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(PrimaryButtonText), "确定");

    /// <summary>关闭按钮文案。</summary>
    public static readonly StyledProperty<string?> CloseButtonTextProperty =
        AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(CloseButtonText), "取消");

    private TaskCompletionSource<bool>? _completion;

    public ConfirmDialog()
    {
        IsVisible = false;
        Focusable = true;
    }

    /// <summary>标题。</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>主按钮文案。</summary>
    public string? PrimaryButtonText
    {
        get => GetValue(PrimaryButtonTextProperty);
        set => SetValue(PrimaryButtonTextProperty, value);
    }

    /// <summary>关闭按钮文案。</summary>
    public string? CloseButtonText
    {
        get => GetValue(CloseButtonTextProperty);
        set => SetValue(CloseButtonTextProperty, value);
    }

    /// <summary>显示对话框并等待用户选择；主按钮为 true，取消 / Esc 为 false。</summary>
    public async Task<bool> ShowAsync()
    {
        if (_completion is not null)
        {
            // 上一次还没收尾（理论上不会发生）：先把旧的一次释放掉，避免调用方永久挂起
            _completion.TrySetResult(false);
        }

        _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsVisible = true;
        Focus();
        return await _completion.Task.ConfigureAwait(true);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_PrimaryButton") is { } primary)
        {
            primary.Click += (_, _) => Complete(true);
        }

        if (e.NameScope.Find<Button>("PART_CloseButton") is { } close)
        {
            close.Click += (_, _) => Complete(false);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Complete(false);
        }
    }

    private void Complete(bool result)
    {
        var completion = _completion;
        _completion = null;
        IsVisible = false;
        completion?.TrySetResult(result);
    }
}
