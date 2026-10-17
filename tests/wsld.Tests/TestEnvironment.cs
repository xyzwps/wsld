using System.Runtime.CompilerServices;
using wsld.Core.I18n;

namespace wsld.Tests;

/// <summary>
/// 单测环境准备:把界面语言钉死为简体中文。
/// 文案抽到 Localizer 之后,断言里写的仍是改版前的中文原句,
/// 不钉语言的话结果会跟着测试机器的系统显示语言变来变去。
/// 语言切换本身的行为由 LocalizerTests 显式覆盖。
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void PinChineseStrings() =>
        Localizer.Default.Language = AppLanguage.ZhCn;
}
