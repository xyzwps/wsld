using System.Text.RegularExpressions;

namespace wsld.Tests;

/// <summary>
/// 功能 012 的防回归闸:XAML 里 Style 的 TargetType 必须和贴它的元素类型对得上。
/// 起因是端口行里的协议下拉框误用了文本框样式,编译器一声不吭,
/// 直到用户点「添加端口」长出新行那一刻才抛 XamlParseException,整个应用直接退出。
/// 这类错只在运行时暴露,所以放在单测里当静态检查用。
/// </summary>
public class XamlStyleTargetTypeTests
{
    /// <summary>XamlControlsResources 自带的样式在本仓库里没有定义,只能把目标类型内置进来。</summary>
    private static readonly Dictionary<string, string> BuiltInStyles = new(StringComparer.Ordinal)
    {
        ["DefaultButtonStyle"] = "Button",
        ["AccentButtonStyle"] = "Button",
        ["SubtleButtonStyle"] = "Button",
    };

    [Fact]
    public void EveryStyleReference_MatchesItsTargetType()
    {
        var findings = new List<string>();
        foreach (var file in SourceXamlFiles())
        {
            findings.AddRange(FindMismatches(Path.GetFileName(file), File.ReadAllText(file)));
        }

        Assert.True(
            findings.Count == 0,
            "Style 的 TargetType 与元素类型不符,模板一实例化就会崩:" +
            Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void Analyzer_FlagsWrongTargetType_AndPassesCorrectOnes()
    {
        // 闸本身也得验一遍:规则写错了却一条都不报,比没有闸更危险。
        const string xaml = """
            <Page>
                <Page.Resources>
                    <Style x:Key="RowBoxStyle" TargetType="TextBox" />
                    <Style x:Key="RowComboStyle" TargetType="ComboBox" />
                </Page.Resources>
                <StackPanel>
                    <TextBox Style="{StaticResource RowBoxStyle}" Width="120" />
                    <ComboBox Style="{StaticResource RowComboStyle}" Width="86" />
                    <ComboBox Style="{StaticResource RowBoxStyle}" Width="86" />
                </StackPanel>
            </Page>
            """;

        var findings = FindMismatches("Sample.xaml", xaml);

        var finding = Assert.Single(findings);
        Assert.Contains("ComboBox", finding, StringComparison.Ordinal);
        Assert.Contains("RowBoxStyle", finding, StringComparison.Ordinal);
        Assert.Contains("TextBox", finding, StringComparison.Ordinal);
    }

    /// <summary>扫一个 XAML 文本,返回所有"元素类型 != 样式 TargetType"的说明。</summary>
    private static IEnumerable<string> FindMismatches(string file, string text)
    {
        // 本文件里定义的样式:key -> TargetType(两种属性顺序都认)
        var styles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(
                     text,
                     """"<Style\b[^>]*?x:Key="([^"]+)"[^>]*?TargetType="([^"]+)"|<Style\b[^>]*?TargetType="([^"]+)"[^>]*?x:Key="([^"]+)""""))
        {
            if (m.Groups[1].Success) styles[m.Groups[1].Value] = m.Groups[2].Value;
            else styles[m.Groups[4].Value] = m.Groups[3].Value;
        }

        // 带引号属性值的元素标签一次吃下,属性换行也认
        foreach (Match m in Regex.Matches(text, """<([A-Za-z][\w.]+)\b((?:"[^"]*"|[^>"])*)/?>"""))
        {
            var element = m.Groups[1].Value;
            var attrMatch = Regex.Match(
                m.Groups[2].Value,
                """Style="\{(?:StaticResource|ThemeResource)\s+([A-Za-z]\w*)""");
            if (!attrMatch.Success) continue;

            var key = attrMatch.Groups[1].Value;
            var targetType = styles.TryGetValue(key, out var local)
                ? local
                : BuiltInStyles.TryGetValue(key, out var builtIn)
                    ? builtIn
                    : null;
            if (targetType is null) continue; // 认不出来的样式(比如外部控件库)不瞎猜

            var expected = ShortName(targetType);
            var actual = ShortName(element);
            if (string.Equals(expected, actual, StringComparison.Ordinal)) continue;

            var line = text.Substring(0, m.Index).Count(c => c == '\n') + 1;
            yield return $"{file}:{line}  <{element}> 贴了 Style={key}(TargetType={targetType})";
        }
    }

    private static string ShortName(string type) => type.Contains(':') ? type.Split(':')[^1] : type;

    /// <summary>仓库源码里的 XAML;打包环境里找不到源码目录就整体跳过。</summary>
    internal static IEnumerable<string> SourceXamlFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wsld.sln")))
        {
            dir = dir.Parent;
        }

        var src = dir is null ? null : Path.Combine(dir.FullName, "src");
        if (src is null || !Directory.Exists(src)) return [];

        return Directory.GetFiles(src, "*.xaml", SearchOption.AllDirectories)
            .Where(f => f.Split(Path.DirectorySeparatorChar).All(segment => segment != "obj" && segment != "bin"));
    }
}