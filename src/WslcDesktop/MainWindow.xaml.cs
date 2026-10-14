using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WslcDesktop.Core.I18n;
using WslcDesktop.Pages;
using WslcDesktop.Services;

namespace WslcDesktop;

/// <summary>
/// 主窗口 = 自定义标题栏 + 左侧导航 + 当前页面(功能 003 · AC11,功能 009 · AC1/AC3/AC5)。
/// 页面实例第一次用到才创建、之后一直留着:切走再切回来不会重新加载,也不会丢搜索条件。
/// 语言切换时例外 —— 静态文案都烧在页面里,只能整页重建(见 <see cref="RebuildPages"/>)。
/// (WinUI 编译限制:x:Bind 的转换器模板无法在 Window 根上生成代码,所以标题栏和导航的文字在代码里赋值。)
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();
    private Page? _current;
    private string _currentKey = "containers";

    public MainWindow()
    {
        InitializeComponent();
        Title = "WSLC Desktop"; // 系统标题栏被我们自己接管了,但任务栏与 UIA 还要靠它认名字

        ConfigureTitleBar();
        ConfigureWindow();

        // 主题:设置里改完立刻换皮,连标题栏按钮的配色一起换。
        AppState.ThemeChanged += (_, _) =>
        {
            ApplyTheme();
            RefreshSettingsPage();
        };
        ApplyTheme();

        // 镜像源:设置页改完,已经缓存着的镜像页要重算"实际会拉哪个引用"(功能 010 · AC7)。
        AppState.MirrorChanged += (_, _) => RefreshImagesMirrorState();

        // 语言:换文案 + 重建当前页。
        Localizer.Default.LanguageChanged += (_, _) =>
        {
            ApplyTexts();
            RebuildPages();
        };
        ApplyTexts();

        // 窗口尺寸变化时重新算标题栏留白(最大化时系统留白会变)。
        Root.SizeChanged += (_, _) => ApplyCaptionInset();

        Nav.SelectedItem = Nav.MenuItems[0]; // 默认停在容器页
    }

    // ---------- 标题栏 ----------

    /// <summary>把系统标题栏收进来,自己那一条 46 高的 Grid 当拖拽区。</summary>
    private void ConfigureTitleBar()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            SetTitleBar(DragHost);
            // 顶条比默认的 32 高,把系统按钮也拉高,才不会出现"按钮靠上、我的按钮居中"的错位。
            titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        }
        catch (Exception ex)
        {
            App.Log("ConfigureTitleBar failed: " + ex);
        }
    }

    /// <summary>右侧动作区让开系统最小化/关闭按钮:留白按 DPI 折算成 DIP。</summary>
    private void ApplyCaptionInset()
    {
        try
        {
            // RightInset 是物理像素,要按当前 DPI 折算成 XAML 用的 DIP。
            var scale = Root.XamlRoot is { } root && root.RasterizationScale > 0
                ? root.RasterizationScale
                : 1.0;
            var inset = AppWindow.TitleBar.RightInset / scale;
            var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            // 最大化时系统内容区四周还会内缩 8 DIP,不留出来按钮会跑到屏幕外。
            TitleActions.Padding = new Thickness(0, 0, Math.Max(0, inset) + (maximized ? 8 : 0), 0);
        }
        catch (Exception ex)
        {
            App.Log("ApplyCaptionInset failed: " + ex);
        }
    }

    /// <summary>应用主题到根元素,并让系统标题栏按钮跟着换配色。</summary>
    private void ApplyTheme()
    {
        Root.RequestedTheme = AppState.RootElementTheme;
        ApplyCaptionColors();
        ApplyCaptionInset();
        UpdateThemeButton();
    }

    private void ApplyCaptionColors()
    {
        try
        {
            var dark = Root.ActualTheme == ElementTheme.Dark;
            var titleBar = AppWindow.TitleBar;
            uint foreground = dark ? 0xFFFFFFFF : 0xE4000000;
            uint inactive = dark ? 0x99FFFFFF : 0x99000000;
            titleBar.ButtonForegroundColor = ToColor(foreground);
            titleBar.ButtonHoverForegroundColor = ToColor(foreground);
            titleBar.ButtonPressedForegroundColor = ToColor(foreground);
            titleBar.ButtonInactiveForegroundColor = ToColor(inactive);
            titleBar.ButtonBackgroundColor = ToColor(0x00000000);
            titleBar.ButtonInactiveBackgroundColor = ToColor(0x00000000);
            titleBar.ButtonHoverBackgroundColor = ToColor(dark ? 0x1AFFFFFFu : 0x11000000);
            titleBar.ButtonPressedBackgroundColor = ToColor(dark ? 0x26FFFFFFu : 0x1F000000);
        }
        catch (Exception ex)
        {
            // 配色属于锦上添花,拿不到就用系统默认,绝不让窗口起不来。
            App.Log("ApplyCaptionColors failed: " + ex);
        }

        static Windows.UI.Color ToColor(uint argb) => new()
        {
            A = (byte)(argb >> 24),
            R = (byte)(argb >> 16),
            G = (byte)(argb >> 8),
            B = (byte)argb,
        };
    }

    private void UpdateThemeButton()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        // 图标表示"当前是什么配色":太阳 = 浅色,月亮 = 深色。
        ThemeGlyph.Glyph = dark ? "\uE708" : "\uE706";
        var key = AppState.Theme switch
        {
            Core.Settings.ThemeMode.Light => "titlebar.theme.light",
            Core.Settings.ThemeMode.Dark => "titlebar.theme.dark",
            _ => dark ? "titlebar.theme.dark" : "titlebar.theme.system",
        };
        var text = Localizer.Default.Get(key);
        ToolTipService.SetToolTip(ThemeButton, text);
        AutomationProperties.SetName(ThemeButton, text);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        AppState.SetTheme(AppState.NextToggledTheme(Root.ActualTheme == ElementTheme.Dark));
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.FooterMenuItems[0];
    }

    private void PaneToggle_Click(object sender, RoutedEventArgs e) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    // ---------- 文案 ----------

    /// <summary>把导航项、工具提示、页标题的文案刷成当前语言。</summary>
    private void ApplyTexts()
    {
        var loc = Localizer.Default;
        SetNavItem(Nav.MenuItems[0], "nav.containers", loc);
        SetNavItem(Nav.MenuItems[1], "nav.images", loc);
        SetNavItem(Nav.MenuItems[2], "nav.run", loc); // 功能 012:命令编排
        SetNavItem(Nav.FooterMenuItems[0], "nav.settings", loc);

        ToolTipService.SetToolTip(PaneToggle, loc.Get("nav.toggle"));
        AutomationProperties.SetName(PaneToggle, loc.Get("nav.toggle"));
        ToolTipService.SetToolTip(SettingsButton, loc.Get("titlebar.settings.tooltip"));
        AutomationProperties.SetName(SettingsButton, loc.Get("titlebar.settings.tooltip"));

        PageTitleText.Text = "· " + loc.Get(PageKeyToNavKey(_currentKey));
        UpdateThemeButton();
    }

    private static void SetNavItem(object item, string key, Localizer loc)
    {
        if (item is not NavigationViewItem nav) return;
        nav.Content = loc.Get(key);
        AutomationProperties.SetName(nav, loc.Get(key));
    }

    private static string PageKeyToNavKey(string key) => key switch
    {
        "images" => "nav.images",
        "run" => "nav.run",
        "settings" => "nav.settings",
        _ => "nav.containers",
    };

    // ---------- 导航 ----------

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string key })
        {
            Show(key);
        }
    }

    private void Show(string key)
    {
        _currentKey = key;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }

        if (ReferenceEquals(_current, page))
        {
            PageTitleText.Text = "· " + Localizer.Default.Get(PageKeyToNavKey(key));
            return;
        }

        _current = page;
        PageHost.Children.Clear();
        PageHost.Children.Add(page);
        PageTitleText.Text = "· " + Localizer.Default.Get(PageKeyToNavKey(key));
    }

    /// <summary>
    /// 标题栏上的主题按钮改的是同一份设置,但设置页可能已经开着(页面是缓存的),
    /// 这里让它把选中态重新读一遍,不然会出现"界面已经是浅色,选项还停在跟随系统"。
    /// </summary>
    private void RefreshSettingsPage()
    {
        if (_pages.TryGetValue("settings", out var page) && page is SettingsPage settings)
        {
            settings.RefreshFromState();
        }
    }

    /// <summary>
    /// 镜像页是缓存的(切走再回来不重建),设置里改镜像源时靠这里把预览文案推平。
    /// </summary>
    private void RefreshImagesMirrorState()
    {
        if (_pages.TryGetValue("images", out var page) && page is ImagesPage images)
        {
            images.ViewModel.RefreshMirrorState();
        }
    }

    private static Page CreatePage(string key) => key switch
    {
        "images" => new ImagesPage(),
        "run" => new RunBuilderPage(),
        "settings" => new SettingsPage(),
        _ => new ContainersPage(),
    };

    /// <summary>
    /// 换语言后重建页面:静态文案烧在页面里,只能整页重来(搜索条件会一起丢)。
    /// 必须排队到下一帧:语言单选按钮的 Checked 事件还在调用栈上,
    /// 当场把整棵可视树拆掉不安全。
    /// </summary>
    private void RebuildPages()
    {
        if (Root.DispatcherQueue is not { } queue)
        {
            RebuildPagesCore();
            return;
        }

        queue.TryEnqueue(RebuildPagesCore);
    }

    private void RebuildPagesCore()
    {
        var key = _currentKey;

        _pages.Clear();
        _current = null;
        Show(key);
    }

    // 尺寸意图一律写成 DIP,再按窗口 DPI 换算后交给 AppWindow —— 它只认物理像素(dev-notes §5)。
    // 容器页左栏是固定 420,右栏详情最少还要 ~400:窗口开小了,详情就会被裁掉半截。
    private const int WindowWidthDip = 1180;
    private const int WindowHeightDip = 780;
    private const int WindowMinWidthDip = 1000;
    private const int WindowMinHeightDip = 640;

    private void ConfigureWindow()
    {
        try
        {
            var scale = WindowScale;
            var appWindow = AppWindow;
            appWindow.Resize(new SizeInt32((int)(WindowWidthDip * scale), (int)(WindowHeightDip * scale)));
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = true;
                presenter.IsResizable = true;
                // 最小尺寸这一对直接收 DIP,不用自己乘缩放。
                presenter.PreferredMinimumWidth = WindowMinWidthDip;
                presenter.PreferredMinimumHeight = WindowMinHeightDip;
            }
            appWindow.Move(new PointInt32(80, 60));
        }
        catch (Exception ex)
        {
            App.Log("ConfigureWindow failed: " + ex);
        }
    }

    /// <summary>窗口所在显示器的缩放比。构造函数里 XamlRoot.RasterizationScale 还没准备好(实测恒为 1),
    /// 只能直接问 Win32 要这个窗口句柄的 DPI —— AppWindow.Id.Value 就是 HWND。</summary>
    private double WindowScale
    {
        get
        {
            try
            {
                var dpi = GetDpiForWindow((nint)AppWindow.Id.Value);
                return dpi > 0 ? dpi / 96d : 1d;
            }
            catch (Exception ex)
            {
                App.Log("WindowScale failed: " + ex);
                return 1d;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}