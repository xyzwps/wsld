using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WslcDesktop.Core;
using WslcDesktop.Core.I18n;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Services;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Pages;

/// <summary>
/// 镜像页(功能 003:列表;功能 004:拉取与删除)。
/// 页面只做呈现与交互约定:回车即拉取、删除前先弹确认框、被占用时把按钮文案交给 Core 决定。
/// 命令拼装、互斥、强制删除策略、错误清洗都在 Core 的 ImagesPageViewModel 里。
/// 与容器页一样不启动自动刷新(功能 003 · AC12)。
/// </summary>
public sealed partial class ImagesPage : Page
{
    private readonly DispatcherQueueTimer _tickTimer;

    public ImagesPageViewModel ViewModel { get; }

    public ImagesPage()
    {
        ViewModel = new ImagesPageViewModel(
            AppServices.Images,
            new DispatcherUiThread(DispatcherQueue));

        // 静态文案的绑定源:页面 DataContext 指向 Localizer。
        DataContext = Localizer.Default;

        InitializeComponent();

        ViewModel.Items.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();

        // 功能 004 · AC7:实测 wslc pull 的输出在进程结束时才冲刷,拿不到真实百分比,
        // 只能让"已等待 N 秒"每秒跳一次;没有操作在跑时计时器自己停下。
        _tickTimer = DispatcherQueue.CreateTimer();
        _tickTimer.Interval = TimeSpan.FromSeconds(1);
        _tickTimer.IsRepeating = true;
        _tickTimer.Tick += (_, _) =>
        {
            if (ViewModel.IsBusy) ViewModel.Tick();
            else _tickTimer.Stop();
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImagesPageViewModel.IsBusy) && ViewModel.IsBusy)
            {
                _tickTimer.Start();
            }
        };

        Loaded += (_, _) =>
        {
            if (ViewModel.IsBusy) _tickTimer.Start();
        };
        Unloaded += (_, _) => _tickTimer.Stop();
    }

    /// <summary>AC14:输入框里按回车等同于点"拉取"。</summary>
    private void PullBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (!ViewModel.PullCommand.CanExecute(null)) return; // 忙碌或输入为空时不响应
        ViewModel.PullCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// 删除按钮走 Click 而不是 Command:确认对话框属于界面层职责(AC14),
    /// 是否使用强制删除由行对象自己决定(AC11),界面不重复这段逻辑。
    /// </summary>
    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ImageItemViewModel item }) return;

        var force = item.ForcePending;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, // 非打包 WinUI 3 必须显式设置,否则 ShowAsync 抛异常
            Title = T(force ? "dialog.delete-image.force-title" : "dialog.delete-image.title"),
            Content = BuildDeleteMessage(item, force),
            PrimaryButtonText = T(force ? "action.force-delete" : "action.delete"),
            CloseButtonText = T("action.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await item.RemoveCommand.ExecuteAsync(null);
    }

    private static object BuildDeleteMessage(ImageItemViewModel item, bool force)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Fmt("dialog.delete-image.body", item.Reference),
            TextWrapping = TextWrapping.Wrap,
        });

        if (force)
        {
            // AC11:强制删除前必须把后果说清楚(实测删除后这些容器再启动会失败)。
            panel.Children.Add(new TextBlock
            {
                Text = Fmt("dialog.delete-image.force-body", item.ContainerCount),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            });
        }
        else if (item.IsInUse)
        {
            panel.Children.Add(new TextBlock
            {
                Text = Fmt("dialog.delete-image.in-use", item.ContainerCount),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = T("dialog.delete-image.untagged"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
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

    /// <summary>把后台线程上的状态更新调度回 UI 线程。</summary>
    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}