using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WslcDesktop.Core;
using WslcDesktop.Core.I18n;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Services;

namespace WslcDesktop.Pages;

/// <summary>
/// 日志页(功能 005)。命令拼装、单进程互斥、攒批上屏、空日志/跟随结束这些语义
/// 全部在 Core 的 LogsPageViewModel 里;页面只负责呈现与"新行到达时滚到底"这类视图职责。
/// </summary>
public sealed partial class LogsPage : Page
{
    /// <summary>跟随时的催屏间隔:慢速日志(每秒几行)等不到攒批阈值,靠它保证"看起来是实时的"。</summary>
    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherQueueTimer _drainTimer;

    public LogsPageViewModel ViewModel { get; }

    public LogsPage()
    {
        ViewModel = new LogsPageViewModel(
            AppServices.Containers,
            AppServices.Logs,
            new DispatcherUiThread(DispatcherQueue));

        // 静态文案的绑定源:页面 DataContext 指向 Localizer。
        DataContext = Localizer.Default;

        InitializeComponent();

        // AC16:自动滚动开着时,每次文本更新都把视图拉到底;关掉后只进缓冲不动视图。
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // AC12:VM 只在攒够 64 行时自己催一次上屏;真机上实测"每秒 1 行"的容器
        // 8 秒都攒不满 64 行,界面会一直停在旧行数(看起来像卡住)。
        // 所以跟随期间由页面用计时器每 500ms 催一次,跟随一结束就停掉,不空转。
        _drainTimer = DispatcherQueue.CreateTimer();
        _drainTimer.Interval = DrainInterval;
        _drainTimer.IsRepeating = true;
        _drainTimer.Tick += (_, _) =>
        {
            ViewModel.DrainPending();
            if (!ViewModel.IsFollowing) _drainTimer.Stop();
        };

        Unloaded += (_, _) => _drainTimer.Stop();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsPageViewModel.IsFollowing) && ViewModel.IsFollowing)
        {
            _drainTimer.Start();
            return;
        }

        if (e.PropertyName != nameof(LogsPageViewModel.LogText) || !ViewModel.AutoScroll) return;

        // 等这一帧布局完成再滚,否则 ScrollableHeight 还是旧内容的值。
        DispatcherQueue.TryEnqueue(ScrollToBottom);
    }

    private void ScrollToBottom()
    {
        if (LogBox.Text.Length == 0) return;
        if (FindScrollViewer(LogBox) is not { } viewer) return;
        viewer.ChangeView(null, viewer.ScrollableHeight, null);
    }

    /// <summary>WinUI 的 TextBox 没有公开的滚动 API,从模板里找它自带的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>下拉选中变化 → 交给 ViewModel(它会决定要不要自动加载一次)。</summary>
    private void Source_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceBox.SelectedItem is LogSourceOptionViewModel option &&
            !ReferenceEquals(ViewModel.SelectedSource, option))
        {
            ViewModel.SelectedSource = option;
        }
    }

    private void Tail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TailBox.SelectedItem is LogsPageViewModel.TailOption tail &&
            !Equals(ViewModel.SelectedTail, tail))
        {
            ViewModel.SelectedTail = tail;
        }
    }

    /// <summary>把后台线程上的状态更新调度回 UI 线程(与容器页/镜像页同款)。</summary>
    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}
