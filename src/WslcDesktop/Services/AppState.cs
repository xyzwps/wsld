using Microsoft.UI.Xaml;
using WslcDesktop.Core.I18n;
using WslcDesktop.Core.Settings;

namespace WslcDesktop.Services;

/// <summary>
/// 应用级设置的运行时状态(功能 009 · AC1/AC2/AC3)。
/// 职责只有三件:开机把设置读出来并落到 Localizer;改主题/改语言时立刻生效并存盘;
/// 把"主题变了"这件事广播给窗口去刷根元素与标题栏配色。存盘失败不影响界面。
/// </summary>
public static class AppState
{
    /// <summary>当前设置;读盘失败时是默认值(主题跟随系统 + 语言跟随系统)。</summary>
    public static AppSettings Current { get; private set; } = new();

    /// <summary>主题变化后触发,窗口据此重设根元素主题与标题栏按钮配色。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>设置文件位置,用于"关于"卡片展示。</summary>
    public static string SettingsFilePath => SettingsStore.FilePath;

    /// <summary>启动时调用一次:读盘 → 应用语言 → 应用主题。</summary>
    public static void Initialize()
    {
        Current = SettingsStore.Load();
        Localizer.Default.Language = Current.Language;
        RaiseThemeChanged();
    }

    public static ThemeMode Theme => Current.Theme;

    /// <summary>根元素该用的主题:跟随系统 = Default,交给框架自己跟。</summary>
    public static ElementTheme RootElementTheme => Current.Theme switch
    {
        ThemeMode.Light => ElementTheme.Light,
        ThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>标题栏主题按钮要切到的目标(在"浅色/深色"之间来回切)。</summary>
    public static ThemeMode NextToggledTheme(bool currentlyDark) =>
        currentlyDark ? ThemeMode.Light : ThemeMode.Dark;

    public static void SetTheme(ThemeMode mode)
    {
        Current.Theme = mode;
        RaiseThemeChanged();
        Save();
    }

    public static AppLanguage Language => Current.Language;

    public static void SetLanguage(AppLanguage language)
    {
        Current.Language = language;
        Localizer.Default.Language = language;
        Save();
    }

    private static void Save() => SettingsStore.Save(Current);

    private static void RaiseThemeChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);
}