using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WslcDesktop.Pages;

namespace WslcDesktop;

/// <summary>
/// 主窗口 = 左侧导航 + 当前页面(功能 003 · AC11)。
/// 页面实例第一次用到才创建、之后一直留着:切走再切回来不会重新加载,也不会丢搜索条件。
/// (WinUI 编译限制:x:Bind 的转换器模板无法在 Window 根上生成代码,所以 x:Bind 都留在 Page 里。)
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();
    private Page? _current;

    public MainWindow()
    {
        InitializeComponent();
        Title = "WSLC Desktop";
        Nav.SelectedItem = Nav.MenuItems[0]; // 默认停在容器页
        ConfigureWindow();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string key })
        {
            Show(key);
        }
    }

    private void Show(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }

        if (ReferenceEquals(_current, page)) return;
        _current = page;
        PageHost.Children.Clear();
        PageHost.Children.Add(page);
    }

    private static Page CreatePage(string key) => key switch
    {
        "images" => new ImagesPage(),
        _ => new ContainersPage(),
    };

    private void ConfigureWindow()
    {
        try
        {
            var appWindow = AppWindow;
            appWindow.Resize(new SizeInt32(1180, 780));
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = true;
                presenter.IsResizable = true;
            }
            appWindow.Move(new PointInt32(80, 60));
        }
        catch (Exception ex)
        {
            App.Log("ConfigureWindow failed: " + ex);
        }
    }
}