using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AnimeDownloader.App.Controls;

/// <summary>
/// 分段选择控件：一组胶囊式选项，选中项以强调渐变高亮。
/// 供 NSFW 过滤、主题等枚举型设置使用。
/// </summary>
public sealed partial class SegmentedControl : UserControl
{
    /// <summary>选项文本集合（ItemsSource）。</summary>
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(System.Collections.IList),
            typeof(SegmentedControl),
            new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>当前选中项索引（-1 表示未选中）。</summary>
    public static readonly DependencyProperty SelectedIndexProperty =
        DependencyProperty.Register(
            nameof(SelectedIndex),
            typeof(int),
            typeof(SegmentedControl),
            new PropertyMetadata(-1, OnSelectedIndexChanged));

    /// <summary>选中项变化事件（仅由用户点击触发）。</summary>
    public event EventHandler<int>? SelectionChanged;

    private bool _syncing;
    private readonly string _groupName = $"seg_{Guid.NewGuid():N}";

    public SegmentedControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Rebuild();
    }

    public System.Collections.IList? ItemsSource
    {
        get => (System.Collections.IList?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SegmentedControl)d).Rebuild();
    }

    private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SegmentedControl)d).SyncCheckState();
    }

    private void Rebuild()
    {
        ItemsHost.Children.Clear();
        ItemsHost.ColumnDefinitions.Clear();
        if (ItemsSource is null)
        {
            return;
        }

        for (var i = 0; i < ItemsSource.Count; i++)
        {
            // 星号列：所有选项均匀占用一样宽的空间
            ItemsHost.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });

            var index = i;
            var radio = new RadioButton
            {
                Content = ItemsSource[i]?.ToString() ?? string.Empty,
                Style = (Style)Application.Current.Resources["AppSegmentRadioStyle"],
                GroupName = _groupName,
                IsChecked = i == SelectedIndex,
            };
            // RadioButton 原生单选互斥：点击未选中项触发 Checked，点击已选中项不会取消
            radio.Checked += (_, _) => OnItemChecked(index);
            Grid.SetColumn(radio, i);
            ItemsHost.Children.Add(radio);
        }
    }

    private void OnItemChecked(int index)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        try
        {
            SelectedIndex = index;
            // RadioButton 同组互斥自动取消其它项；这里再强制同步一次确保一致
            SyncCheckState();
            SelectionChanged?.Invoke(this, index);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncCheckState()
    {
        for (var i = 0; i < ItemsHost.Children.Count; i++)
        {
            if (ItemsHost.Children[i] is RadioButton radio)
            {
                radio.IsChecked = i == SelectedIndex;
            }
        }
    }
}