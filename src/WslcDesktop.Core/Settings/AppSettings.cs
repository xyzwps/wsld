using WslcDesktop.Core.I18n;

namespace WslcDesktop.Core.Settings;

/// <summary>用户可改的应用设置;新增字段一律给默认值,老配置文件才能继续读。</summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;

    public AppLanguage Language { get; set; } = AppLanguage.FollowSystem;
}