using System.Globalization;
using Microsoft.UI.Xaml.Data;
using WslcDesktop.Core.I18n;

namespace WslcDesktop.Converters;

/// <summary>
/// 文案键 → 当前语言的句子(功能 009 · AC4/AC5)。
/// 绑定值一律忽略,键从 ConverterParameter 取:
///   ConverterParameter="action.refresh"            → 直接取整句
///   ConverterParameter="interval.seconds|2"        → {0} 用 2 填(能解析成数字就当数字)
/// 页面 DataContext 固定为 Localizer.Default:语言变化时它的 Version 会跳,
/// 静态文案因此能重新求值;就算不跳,主窗口也会在当前语言变化后重建页面。
/// </summary>
public sealed class LocalizationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = parameter as string;
        if (string.IsNullOrEmpty(key)) return "";

        var separator = key.IndexOf('|');
        if (separator < 0) return Localizer.Default.Get(key);

        // key|arg0|arg1 … → 带占位符的文案
        var parts = key.Split('|');
        var args = new object[parts.Length - 1];
        for (var i = 1; i < parts.Length; i++)
        {
            var raw = parts[i];
            args[i - 1] = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
                ? num
                : raw;
        }

        return Localizer.Default.Fmt(parts[0], args);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}