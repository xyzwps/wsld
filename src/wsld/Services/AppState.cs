using Microsoft.UI.Xaml;
using wsld.Core.I18n;
using wsld.Core.Settings;

namespace wsld.Services;

/// <summary>
/// 应用级设置的运行时状态(功能 009 · AC1/AC2/AC3,功能 010 · AC7)。
/// 职责只有三件:开机把设置读出来并落到 Localizer;改主题/改语言时立刻生效并存盘;
/// 把"主题变了"这件事广播给窗口去刷根元素与标题栏配色。存盘失败不影响界面。
/// </summary>
public static class AppState
{
    /// <summary>当前设置;读盘失败时是默认值(主题跟随系统 + 语言跟随系统)。</summary>
    public static AppSettings Current { get; private set; } = new();

    /// <summary>主题变化后触发,窗口据此重设根元素主题与标题栏按钮配色。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>镜像源设置变化后触发(功能 010 · AC7)。页面是缓存的,镜像页要靠这个事件重算"实际会拉哪个引用"。</summary>
    public static event EventHandler? MirrorChanged;

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

    /// <summary>镜像源地址原样(用户输入什么存什么,规范化交给 RegistryMirrorPolicy)。</summary>
    public static string RegistryMirror => Current.RegistryMirror;

    /// <summary>
    /// 设置镜像源(功能 010 · AC7):只去首尾空白,内部原样保存 ——
    /// 带不带 https://、大小写,都由使用方规范化,界面上看到的永远是用户自己写的那串。
    /// </summary>
    public static void SetRegistryMirror(string? mirror)
    {
        var value = mirror?.Trim() ?? "";
        if (string.Equals(Current.RegistryMirror, value, StringComparison.Ordinal)) return;

        Current.RegistryMirror = value;
        Save();
        RaiseMirrorChanged();
    }

    public static bool RenameMirrorPull => Current.RenameMirrorPull;

    /// <summary>是否把走镜像源拉下来的镜像改名回用户输入的名字(功能 010 · AC5)。</summary>
    public static void SetRenameMirrorPull(bool enabled)
    {
        if (Current.RenameMirrorPull == enabled) return;

        Current.RenameMirrorPull = enabled;
        Save();
        RaiseMirrorChanged();
    }

    private static void Save() => SettingsStore.Save(Current);

    private static void RaiseThemeChanged() => ThemeChanged?.Invoke(null, EventArgs.Empty);

    private static void RaiseMirrorChanged() => MirrorChanged?.Invoke(null, EventArgs.Empty);
}