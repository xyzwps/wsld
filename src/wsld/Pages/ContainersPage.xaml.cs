using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using wsld.Core;
using wsld.Core.I18n;
using wsld.Core.ViewModels;
using wsld.Services;
using wsld.Ui;
using static wsld.Core.I18n.Loc;

namespace wsld.Pages;

/// <summary>
/// 容器列表页(左栏) + 容器详情区(右栏,两个页签:详情 / 日志)分栏布局。
/// 功能 001:列表/搜索/自动刷新;功能 002:生命周期操作;功能 005:日志页签的两种读法。
/// </summary>
public sealed partial class ContainersPage : Page, INotifyPropertyChanged
{
    /// <summary>低速率日志的催屏间隔:攒不满一批时也要让用户看见"确实还在收"。</summary>
    private static readonly TimeSpan LogFlushInterval = TimeSpan.FromMilliseconds(500);

    public ContainersPageViewModel ViewModel { get; }
    public ContainerDetailViewModel? DetailViewModel { get; private set; }

    /// <summary>日志页签的 VM 与详情 VM 不同:页面一份就够,换容器靠 ShowContainer 复位,
    /// 免得每次点击都把已收下的行和正在跑的跟随进程一起扔掉。</summary>
    public ContainerLogsViewModel LogsViewModel { get; }

    /// <summary>右栏当前页签:false=详情,true=日志。</summary>
    public bool IsLogsTab { get; private set; }

    /// <summary>详情内容可见 = 有详情且停在详情页签(两块内容同占一行,靠可见性切换)。</summary>
    public bool DetailContentVisible => HasDetail && !IsLogsTab;

    public bool LogsContentVisible => HasDetail && IsLogsTab;

    /// <summary>右栏详情面板是否可见。BoolToVis 只认真 bool(见其类注释),
    /// 直接把 DetailViewModel 对象丢给它永远得到 false,右栏就再也出不来,
    /// 所以这里单独开一个 HasDetail 供四处 Visibility 使用。</summary>
    public bool HasDetail => DetailViewModel is not null;

    public bool DetailHasError => DetailViewModel?.HasError ?? false;
    public string? DetailError => DetailViewModel?.Error;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly DispatcherUiThread _uiThread;
    private readonly DispatcherQueueTimer _logFlushTimer;
    private bool _restoringSelection;

    public ContainersPage()
    {
        _uiThread = new DispatcherUiThread(DispatcherQueue);
        ViewModel = new ContainersPageViewModel(AppServices.Containers, _uiThread);
        LogsViewModel = new ContainerLogsViewModel(AppServices.Logs, _uiThread);
        LogsViewModel.InitializeCommands();

        _logFlushTimer = DispatcherQueue.CreateTimer();
        _logFlushTimer.Interval = LogFlushInterval;
        _logFlushTimer.IsRepeating = true;
        _logFlushTimer.Tick += (_, _) => LogsViewModel.DrainPending();

        DataContext = Localizer.Default;
        InitializeComponent();

        // 行数下拉的初值跟 VM 的默认档(200 行)对齐:光靠 ItemsSource 赋值,
        // ComboBox 会自己选中第一项(100 行),界面显示的和实际发的参数就不是一个数。
        TailBox.SelectedItem = LogsViewModel.SelectedTail;

        LogsViewModel.PropertyChanged += OnLogsViewModelPropertyChanged;
        LogsViewModel.LogTextChangedUi += (_, _) =>
        {
            // 布局完成后再滚,否则 ScrollableHeight 还是上一批行的值(和拉取进度区同一个坑)。
            if (LogsViewModel.AutoScroll) DispatcherQueue.TryEnqueue(() => TextBoxScroller.ScrollToBottom(LogBox));
        };

        ViewModel.Items.CollectionChanged += (_, _) =>
        {
            UpdateEmptyHint();
            RestoreSelection();
        };
        UpdateEmptyHint();

        Loaded += (_, _) => ViewModel.StartAutoRefresh();
        Unloaded += (_, _) =>
        {
            ViewModel.StopAutoRefresh();
            LogsViewModel.StopFollowing();
            _logFlushTimer.Stop();
        };
    }

    /// <summary>实时日志进行中才需要催屏定时器:每来一行就重绘一次整块文本会被攒批挡掉,
    /// 但低速率日志(实测每秒一行)攒不满一批,没有定时器看着就像"卡住了"。</summary>
    private void OnLogsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContainerLogsViewModel.IsFollowing))
        {
            if (LogsViewModel.IsFollowing) _logFlushTimer.Start();
            else _logFlushTimer.Stop();
        }
    }

    private async void DetailRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DetailViewModel is null) return;

        var running = DetailViewModel.IsRunning;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = T("dialog.delete-container.title"),
            Content = BuildDeleteMessage(DetailViewModel.DisplayName, running),
            PrimaryButtonText = T("action.delete"),
            CloseButtonText = T("action.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await DetailViewModel.RemoveCommand.ExecuteAsync(null);
    }

    private static object BuildDeleteMessage(string name, bool running)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Fmt("dialog.delete-container.body", name),
            TextWrapping = TextWrapping.Wrap,
        });
        if (running)
        {
            panel.Children.Add(new TextBlock
            {
                Text = T("dialog.delete-container.running"),
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

    /// <summary>选中项变化 = 用户在列表里点了一行(也可能是按方向键挪了焦点)。</summary>
    private void ContainerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 自动刷新后 RestoreSelection 把选中项放回去,那次不算"用户点击",别跟着动滚动条。
        if (_restoringSelection) return;

        if (e.AddedItems.Count > 0)
        {
            // 滚到"这次刚被选中的那一项":ScrollIntoView 收数据项本身。
            // 别拿 AddedItems 的下标去索引 Items(那是"新增列表里的第几个",单选恒为 0 = 第一行);
            // 也别用 SelectedItem —— 它可能还是 RestoreSelection 刚放回去的旧那一行。
            ContainerList.ScrollIntoView(e.AddedItems[0]);
        }

        if (ContainerList.SelectedItem is ContainerItemViewModel item) ShowDetail(item);
    }

    /// <summary>
    /// Items 被整批重建时(容器状态变了会重新排序、搜索词变了会过滤),ListView 把选中项清成 null,
    /// 可右栏还在显示这个容器 —— 高亮跟着详情走,别让两边各说各话。
    /// </summary>
    private void RestoreSelection()
    {
        var id = DetailViewModel?.ContainerId;
        if (string.IsNullOrEmpty(id)) return;

        var match = ViewModel.Items.FirstOrDefault(item => item.Id == id);
        if (match is null || ReferenceEquals(ContainerList.SelectedItem, match)) return;

        _restoringSelection = true;
        ContainerList.SelectedItem = match;
        _restoringSelection = false;
    }

    /// <summary>换容器时旧详情对象的三个事件要先摘掉,否则每点一次就漏一个订阅在页面上。</summary>
    private void DetachDetail()
    {
        if (DetailViewModel is null) return;
        DetailViewModel.Removed -= OnDetailRemoved;
        DetailViewModel.RefreshRequested -= OnDetailRefreshRequested;
        DetailViewModel.PropertyChanged -= OnDetailPropertyChanged;
    }

    private void OnDetailPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ContainerDetailViewModel.HasError) or nameof(ContainerDetailViewModel.Error))
        {
            OnPropertyChanged(nameof(DetailHasError));
            OnPropertyChanged(nameof(DetailError));
        }

        // 容器起停会改「实时日志」能不能开(实测只有运行中的容器才收得到新行)。
        if (args.PropertyName == nameof(ContainerDetailViewModel.IsRunning) && DetailViewModel is { } detail)
        {
            LogsViewModel.UpdateRunning(detail.IsRunning);
        }
    }

    private void ShowDetail(ContainerItemViewModel item)
    {
        var containerId = item.Id;
        // 重复点同一行不重建:右栏会白闪一下,自动刷新还可能把选中项弄丢。
        if (DetailViewModel?.ContainerId == containerId) return;

        DetachDetail();
        DetailViewModel = new ContainerDetailViewModel(AppServices.Containers, _uiThread);
        DetailViewModel.InitializeCommands(_ => Task.CompletedTask);
        DetailViewModel.Removed += OnDetailRemoved;
        DetailViewModel.RefreshRequested += OnDetailRefreshRequested;
        DetailViewModel.PropertyChanged += OnDetailPropertyChanged;

        // 换容器 = 日志另起一次会话:停掉旧容器的实时日志,清掉它的行。
        SelectDetailTab();
        LogsViewModel.ShowContainer(containerId, item.IsRunning);

        OnPropertyChanged(nameof(DetailViewModel));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailHasError));
        OnPropertyChanged(nameof(DetailError));

        _ = DetailViewModel.LoadAsync(containerId);
    }

    /// <summary>页签回到「详情」。列表里点别的容器时不该带着上一个容器的实时日志继续跑。</summary>
    private void SelectDetailTab()
    {
        if (!IsLogsTab && DetailTabRadio?.IsChecked != true) return;

        IsLogsTab = false;
        LogsViewModel.StopFollowing();
        if (DetailTabRadio is not null) DetailTabRadio.IsChecked = true;
        OnPropertyChanged(nameof(IsLogsTab));
        OnPropertyChanged(nameof(DetailContentVisible));
        OnPropertyChanged(nameof(LogsContentVisible));
    }

    private void DetailTab_Checked(object sender, RoutedEventArgs e)
    {
        var logs = ReferenceEquals(sender, LogsTabRadio);
        if (logs == IsLogsTab) return;

        IsLogsTab = logs;
        if (!logs) LogsViewModel.StopFollowing();
        OnPropertyChanged(nameof(IsLogsTab));
        OnPropertyChanged(nameof(DetailContentVisible));
        OnPropertyChanged(nameof(LogsContentVisible));

        if (logs) DispatcherQueue.TryEnqueue(() => TextBoxScroller.ScrollToBottom(LogBox));
    }

    private void Tail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: LogTailOption option }) LogsViewModel.SelectedTail = option;
    }

    private void OnDetailRemoved(object? sender, EventArgs e)
    {
        DetachDetail();
        DetailViewModel = null;
        SelectDetailTab();
        LogsViewModel.ShowContainer("", isRunning: false); // 容器没了,这一次日志会话跟着收摊
        OnPropertyChanged(nameof(DetailViewModel));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(DetailHasError));
        OnPropertyChanged(nameof(DetailError));
        OnPropertyChanged(nameof(DetailContentVisible));
        OnPropertyChanged(nameof(LogsContentVisible));
        _ = ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void OnDetailRefreshRequested(object? sender, EventArgs e)
    {
        if (DetailViewModel is not null)
        {
            _ = DetailViewModel.LoadAsync(DetailViewModel.ContainerId);
        }
        _ = ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}
