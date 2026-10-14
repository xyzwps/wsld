using System.Text.RegularExpressions;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 012 的第二道 XAML 闸:ComboBox 的候选项不要用"内联 ComboBoxItem + 绑定内容"。
/// 实测:那种写法的 Content 要延后求值,ComboBox 初始化时抄进选框的是空内容 ——
/// 序号已经选中 0,框里却什么都不显示,只有把下拉列表点开过一次才有字。
/// 换成纯字符串(ItemsSource 或 x:String 子项)就正常,所以把这条规矩钉成单测。
/// </summary>
public class XamlComboBoxItemContentTests
{
    [Fact]
    public void NoInlineComboBoxItem_WithBoundContent()
    {
        var findings = new List<string>();
        foreach (var file in XamlStyleTargetTypeTests.SourceXamlFiles())
        {
            findings.AddRange(FindDeferredItems(Path.GetFileName(file), File.ReadAllText(file)));
        }

        Assert.True(
            findings.Count == 0,
            "内联 ComboBoxItem 会让下拉框的选框空着,候选项请改成纯字符串:" +
            Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    [Fact]
    public void Analyzer_FlagsBoundItemContent_AndPassesPlainStrings()
    {
        // 闸本身也得验一遍:规则写错了却一条都不报,比没有闸更危险。
        const string xaml = """
            <Page>
                <ComboBox SelectedIndex="0">
                    <ComboBoxItem Content="{Binding Foo}" />
                    <ComboBoxItem Content="always" />
                </ComboBox>
                <ComboBox ItemsSource="{x:Bind Options}" SelectedIndex="0" />
            </Page>
            """;

        var finding = Assert.Single(FindDeferredItems("Sample.xaml", xaml));
        Assert.Contains("ComboBoxItem", finding, StringComparison.Ordinal);
        Assert.Contains(":3", finding, StringComparison.Ordinal);
    }

    /// <summary>扫一个 XAML 文本,返回所有"内联 ComboBoxItem 且 Content 是绑定"的说明。</summary>
    private static IEnumerable<string> FindDeferredItems(string file, string text)
    {
        foreach (Match m in Regex.Matches(text, """<ComboBoxItem\b((?:"[^"]*"|[^>"])*)/?>"""))
        {
            if (!Regex.IsMatch(m.Groups[1].Value, """Content="\{(?:Binding|x:Bind)""")) continue;

            var line = text.Substring(0, m.Index).Count(c => c == '\n') + 1;
            yield return $"{file}:{line}  <ComboBoxItem> 的 Content 是绑定";
        }
    }
}
