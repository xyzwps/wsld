using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WslcDesktop.Core.I18n;
using WslcDesktop.Core.Settings;
using WslcDesktop.Services;

namespace WslcDesktop.Pages;

/// <summary>
/// 设置页(功能 009 · AC2/AC3/AC5):主题、语言、运行环境信息。
/// 选中即生效、即存盘,没有"保存"按钮 —— 少一步,也免得改了没存。
/// 换语言会触发主窗口重建页面,本页会被换成一个新实例,文案因此永远是当前语言的。
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>构造时回填选中项会触发 Checked,用这个标记挡住"自己保存自己"。</summary>
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        ApplyTexts();
    }

    /// <summary>外部(标题栏主题按钮)改过设置后,让本页的选中态跟上。</summary>
    public void RefreshFromState() => ApplyTexts();

    /// <summary>把界面文案刷成当前语言(页面重建时天然就是最新语言)。</summary>
    private void ApplyTexts()
    {
        var loc = Localizer.Default;
        _loading = true;

        HeaderText.Text = loc.Get("nav.settings");
        SubtitleText.Text = loc.Get("settings.subtitle");

        AppearanceHeader.Text = loc.Get("settings.appearance");
        ThemeLabel.Text = loc.Get("settings.theme");
        ThemeSystemOption.Content = loc.Get("settings.theme.system");
        ThemeLightOption.Content = loc.Get("settings.theme.light");
        ThemeDarkOption.Content = loc.Get("settings.theme.dark");
        ThemeHintText.Text = loc.Get("settings.theme.hint");

        LanguageHeader.Text = loc.Get("settings.language");
        LanguageSystemOption.Content = loc.Get("settings.language.system");
        LanguageZhOption.Content = loc.Get("settings.language.zh");
        LanguageEnOption.Content = loc.Get("settings.language.en");
        LanguageCurrentText.Text = loc.Fmt("settings.language.hint", loc.LanguageDisplayName);
        LanguageNoteText.Text = loc.Get("settings.language.note");

        AboutHeader.Text = loc.Get("settings.about");
        VersionLabel.Text = loc.Get("settings.app-version");
        VersionValue.Text = ResolveAppVersion();
        WslcLabel.Text = loc.Get("settings.wslc-path");
        WslcValue.Text = AppServices.WslcPath ?? loc.Get("settings.wslc-not-found");
        FileLabel.Text = loc.Get("settings.settings-file");
        FileValue.Text = AppState.SettingsFilePath;

        ThemeSystemOption.IsChecked = AppState.Theme == ThemeMode.FollowSystem;
        ThemeLightOption.IsChecked = AppState.Theme == ThemeMode.Light;
        ThemeDarkOption.IsChecked = AppState.Theme == ThemeMode.Dark;

        LanguageSystemOption.IsChecked = AppState.Language == AppLanguage.FollowSystem;
        LanguageZhOption.IsChecked = AppState.Language == AppLanguage.ZhCn;
        LanguageEnOption.IsChecked = AppState.Language == AppLanguage.En;

        _loading = false;
    }

    private void Option_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } button) return;

        if (button == ThemeSystemOption || button == ThemeLightOption || button == ThemeDarkOption)
        {
            AppState.SetTheme(Enum.Parse<ThemeMode>(tag));
            return;
        }

        var language = Enum.Parse<AppLanguage>(tag);
        if (language == AppState.Language) return;
        AppState.SetLanguage(language);
    }

    private static string ResolveAppVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}