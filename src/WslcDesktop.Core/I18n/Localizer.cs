using System.ComponentModel;
using System.Globalization;

namespace WslcDesktop.Core.I18n;

/// <summary>
/// 界面文案入口(功能 009 · AC5)。
/// 语言默认跟随系统显示语言,系统语言不是中文时退回英文;
/// 文案表在 <see cref="StringTables"/> 里,中英两套,键名相同。
/// 切语言会触发 <see cref="LanguageChanged"/>,界面据此重建页面与文案。
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    /// <summary>全局唯一实例:Core 的模型与界面都读它。</summary>
    public static Localizer Default { get; } = new();

    private AppLanguage _language = AppLanguage.FollowSystem;

    /// <summary>语言每次变化 +1。界面里的静态文案绑定这个属性,变化时自动重新求值。</summary>
    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>语言变化(含"跟随系统"下解析结果不变但用户重新指定)。</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>用户选择的语言;FollowSystem 表示跟着系统走。</summary>
    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;

            _language = value;
            Version++;
            Raise();
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>真正用来取文案的语言。</summary>
    public ResolvedLanguage Resolved => Resolve(_language);

    /// <summary>当前语言的自称(简体中文 / English),用于"当前界面语言:xx"这类提示。</summary>
    public string LanguageDisplayName => Get(Resolved == ResolvedLanguage.ZhCn ? "language.name.zh" : "language.name.en");

    /// <summary>当前语言的 BCP-47 标记,用于日期等格式化。</summary>
    public string LocaleName => Resolved == ResolvedLanguage.ZhCn ? "zh-CN" : "en-US";

    /// <summary>取文案;缺键时退回中文,再缺键时把键名原样返回,方便一眼看出漏翻。</summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";

        var table = StringTables.For(Resolved);
        if (table.TryGetValue(key, out var text)) return text;
        if (StringTables.ZhCn.TryGetValue(key, out var fallback)) return fallback;
        return key;
    }

    /// <summary>带参数的文案。数字一律按不变文化格式化,免得区域设置影响显示。</summary>
    public string Fmt(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(key), args);

    public string this[string key] => Get(key);

    /// <summary>系统显示语言是不是中文。</summary>
    public static bool SystemUiIsChinese() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public static ResolvedLanguage Resolve(AppLanguage language) => language switch
    {
        AppLanguage.ZhCn => ResolvedLanguage.ZhCn,
        AppLanguage.En => ResolvedLanguage.En,
        _ => SystemUiIsChinese() ? ResolvedLanguage.ZhCn : ResolvedLanguage.En,
    };

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }
}

/// <summary>给 Core 用的极简取句入口:<c>using static WslcDesktop.Core.I18n.Loc;</c>。</summary>
public static class Loc
{
    public static string T(string key) => Localizer.Default.Get(key);

    public static string Fmt(string key, params object[] args) => Localizer.Default.Fmt(key, args);
}