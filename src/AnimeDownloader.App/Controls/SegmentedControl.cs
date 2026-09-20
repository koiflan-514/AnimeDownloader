using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 分段选择控件：一组等宽选项，选中项以强调色底衬高亮。供 NSFW 过滤、主题等枚举型设置使用。
///
/// Avalonia 版与 WinUI 版的差异只在「怎么长出来」：
///     WinUI 版是一个 UserControl，XAML 里放一个空 Grid，代码在 Loaded 时往 Grid 里塞 RadioButton。
///     Avalonia 版改成 <see cref="TemplatedControl"/>：外壳（1px 凹槽）由
///     <c>AppSegmentedControl</c> 主题的模板提供，填充发生在 OnApplyTemplate。
///     这样做的好处是「控件本身不再自带一份固定外观」—— 换主题就是换 ControlTheme，
///     与其余控件的机制一致；也避免 UserControl 里再套一层 x:Name 的命名作用域。
///
/// 选项等宽靠 Grid 的星号列：每项一列、列宽 <c>*</c>，因此「当前选项」永远是等宽格子里的一个，
/// 不会因为文字长短把分段器撑歪。
/// </summary>
public sealed class SegmentedControl : TemplatedControl
{
    /// <summary>选项文本集合（ItemsSource）。</summary>
    public static readonly StyledProperty<IList?> ItemsSourceProperty =
        AvaloniaProperty.Register<SegmentedControl, IList?>(nameof(ItemsSource));

    /// <summary>当前选中项索引（-1 表示未选中）。</summary>
    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<SegmentedControl, int>(nameof(SelectedIndex), -1);

    private Grid? _itemsHost;

    /// <summary>选中项变化事件（仅由用户点击触发）。</summary>
    public event EventHandler<int>? SelectionChanged;

    /// <summary>选项文本集合。</summary>
    public IList? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>当前选中项索引（-1 表示未选中）。</summary>
    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _itemsHost = e.NameScope.Find<Grid>("PART_ItemsHost");
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            Rebuild();
        }
        else if (change.Property == SelectedIndexProperty)
        {
            SyncCheckState();
        }
    }

    private void Rebuild()
    {
        if (_itemsHost is null)
        {
            return;
        }

        _itemsHost.Children.Clear();
        _itemsHost.ColumnDefinitions.Clear();
        if (ItemsSource is null)
        {
            return;
        }

        // 同组互斥：GroupName 必须每个实例唯一，否则页面上两个分段器会互相取消选中。
        var groupName = $"seg_{Guid.NewGuid():N}";
        for (var i = 0; i < ItemsSource.Count; i++)
        {
            // 星号列：所有选项均匀占用一样宽的空间
            _itemsHost.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var index = i;
            var radio = new RadioButton
            {
                Content = ItemsSource[i]?.ToString() ?? string.Empty,
                Theme = AppTheme.Lookup("AppSegmentRadio"),
                GroupName = groupName,
                IsChecked = i == SelectedIndex,
            };
            // RadioButton 原生单选互斥：点击未选中项会置 IsChecked=true。
            // 注意 Avalonia 的 ToggleButton **没有** WPF 那对 Checked/Unchecked 事件，
            // 只有 IsCheckedChanged（bool? 三态都会经过它），因此这里自己判方向。
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    OnItemChecked(index);
                }
            };
            Grid.SetColumn(radio, i);
            _itemsHost.Children.Add(radio);
        }
    }

    private void OnItemChecked(int index)
    {
        if (SelectedIndex == index)
        {
            return;
        }

        SelectedIndex = index;
        // RadioButton 同组互斥已经自动取消了其它项；这里再同步一次确保一致。
        SyncCheckState();
        SelectionChanged?.Invoke(this, index);
    }

    private void SyncCheckState()
    {
        if (_itemsHost is null)
        {
            return;
        }

        for (var i = 0; i < _itemsHost.Children.Count; i++)
        {
            if (_itemsHost.Children[i] is RadioButton radio)
            {
                radio.IsChecked = i == SelectedIndex;
            }
        }
    }
}
