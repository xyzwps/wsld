using Microsoft.UI.Dispatching;
using WslcDesktop.Core;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Services;
using Microsoft.UI.Xaml.Controls;

namespace WslcDesktop.Pages;

/// <summary>
/// 镜像列表页(功能 003)。
/// 页面只做呈现:命令拼装、排序、过滤、错误清洗都在 Core 的 ImagesPageViewModel 里。
/// 与容器页不同,这里不启动自动刷新(AC12)—— 镜像不会因为等在那里就自己变。
/// </summary>
public sealed partial class ImagesPage : Page
{
    public ImagesPageViewModel ViewModel { get; }

    public ImagesPage()
    {
        ViewModel = new ImagesPageViewModel(
            AppServices.Images,
            new DispatcherUiThread(DispatcherQueue));

        InitializeComponent();

        ViewModel.Items.CollectionChanged += (_, _) => UpdateEmptyHint();
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = ViewModel.Items.Count == 0 && !ViewModel.HasError
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    /// <summary>把后台线程上的状态更新调度回 UI 线程。</summary>
    private sealed class DispatcherUiThread(DispatcherQueue queue) : IUIThread
    {
        public void Post(Action action) => queue.TryEnqueue(() => action());
    }
}