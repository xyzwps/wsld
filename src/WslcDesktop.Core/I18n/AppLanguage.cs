namespace WslcDesktop.Core.I18n;

/// <summary>
/// 用户可选的界面语言(功能 009 · AC5)。
/// FollowSystem = 跟随系统显示语言,系统语言没有对应文案时退回英语。
/// </summary>
public enum AppLanguage
{
    FollowSystem = 0,
    ZhCn = 1,
    En = 2,
}

/// <summary>解析后的实际语言 —— 目前只有简体中文和英文两套文案。</summary>
public enum ResolvedLanguage
{
    ZhCn = 0,
    En = 1,
}