using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslcDesktop.Core;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Converters;
using WslcDesktop.Services;

namespace WslcDesktop.Pages;

/// <summary>容器列表页(功能 001):启动即加载、搜索/过滤、自动刷新、错误展示。</summary>
public sealed partial class ContainersPage : Page
{
    public ContainersPageViewModel ViewModel { get; }

    public ContainersPage()
    {
        ViewModel = new ContainersPageViewModel(
            AppServices.Containers,
            new DispatcherUiThread(DispatcherQueue));

        InitializeComponent();

        // 转换器同时注册到页面资源,确保模板里的 StaticResource 一定能解析到。
        Resources["BoolToVis"] = new BoolToVisibilityConverter();

        IntervalBox.SelectedIndex = 2; // 默认 5 秒,与 ViewModel 默认值一致
        ViewModel.Items.CollectionChanged += Items_CollectionChanged;
        UpdateEmptyHint();

        Loaded += (_, _) => ViewModel.StartAutoRefresh();
        Unloaded += (_, _) => ViewModel.StopAutoRefresh();
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateEmptyHint();

    private void UpdateEmptyHint() =>
        EmptyHint.Visibility = ViewModel.Items.Count == 0 && !ViewModel.HasError
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox?.SelectedItem is ComboBoxItem { Tag: not null } item &&
            int.TryParse(item.Tag.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            ViewModel.AutoRefreshSeconds = seconds;
        }
    }

    /// <summary>把后台线程上的状态更新调度回 UI 线程。</summary>
    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}