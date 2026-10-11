using System.Globalization;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslcDesktop.Core;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Services;

namespace WslcDesktop.Pages;

/// <summary>
/// 容器列表页。功能 001:列表/搜索/自动刷新;功能 002:行内生命周期操作。
/// 页面只负责呈现与交互约定(删除前确认),真正的命令拼装与状态编排都在 Core 的 ViewModel 里。
/// </summary>
public sealed partial class ContainersPage : Page
{
    public ContainersPageViewModel ViewModel { get; }

    public ContainersPage()
    {
        ViewModel = new ContainersPageViewModel(
            AppServices.Containers,
            new DispatcherUiThread(DispatcherQueue));

        InitializeComponent();

        IntervalBox.SelectedIndex = 2; // 默认 5 秒,与 ViewModel 默认值一致
        ViewModel.Items.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();

        Loaded += (_, _) => ViewModel.StartAutoRefresh();
        Unloaded += (_, _) => ViewModel.StopAutoRefresh();
    }

    /// <summary>
    /// 删除按钮走 Click 而不是 Command:确认对话框属于界面层职责(功能 002 · AC10),
    /// 确认之后再交给 RemoveCommand —— 由它决定是否使用强制删除,界面不重复这段逻辑。
    /// </summary>
    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ContainerItemViewModel item }) return;

        var running = item.IsRunning;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, // 非打包 WinUI 3 必须显式设置,否则 ShowAsync 抛异常
            Title = "删除容器",
            Content = BuildDeleteMessage(item.DisplayName, running),
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await item.RemoveCommand.ExecuteAsync(null);
    }

    private static object BuildDeleteMessage(string name, bool running)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"确定要删除容器 {name} 吗?此操作不可恢复。",
            TextWrapping = TextWrapping.Wrap,
        });
        if (running)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "该容器正在运行,将强制停止并删除(wslc remove -f)。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)
                    Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBrush"],
            });
        }
        return panel;
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = ViewModel.Items.Count == 0 && !ViewModel.HasError
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

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