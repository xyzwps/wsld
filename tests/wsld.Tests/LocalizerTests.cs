using System.Text.RegularExpressions;
using wsld.Core.I18n;

namespace wsld.Tests;

/// <summary>
/// 功能 009 · AC5:文案表与语言切换的行为。
/// 这里一律用独立的 Localizer 实例,不动 Localizer.Default ——
/// 别的测试类的断言写的是中文原句,并行跑的时候不能有人把全局语言改走。
/// </summary>
public class LocalizerTests
{
    [Fact]
    public void ZhAndEnTables_HaveExactlyTheSameKeys()
    {
        var zh = StringTables.ZhCn.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var en = StringTables.En.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        Assert.Equal(zh, en);
        Assert.NotEmpty(zh);
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    public void Tables_HaveNoEmptyValues(string which)
    {
        var table = which == "zh" ? StringTables.ZhCn : StringTables.En;
        Assert.All(table, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key + " 是空的"));
    }

    [Fact]
    public void ZhAndEnTables_UseTheSamePlaceholders()
    {
        foreach (var (key, zhText) in StringTables.ZhCn)
        {
            var enText = StringTables.En[key];
            Assert.Equal(PlaceholderIndices(zhText), PlaceholderIndices(enText));
        }
    }

    [Fact]
    public void Tables_OnlyUseNumberedPlaceholders()
    {
        // 花括号里写了非数字的东西,string.Format 当场抛异常,这里提前拦住。
        foreach (var (key, text) in StringTables.ZhCn.Concat(StringTables.En))
        {
            foreach (Match match in Regex.Matches(text, @"\{([^}]*)\}"))
            {
                var body = match.Groups[1].Value;
                Assert.True(body.Length > 0 && body.All(char.IsDigit),
                    key + " 里有非法占位符:" + match.Value);
            }
        }
    }

    [Fact]
    public void ExplicitEnglish_ReturnsEnglishStrings()
    {
        var loc = new Localizer { Language = AppLanguage.En };

        Assert.Equal(ResolvedLanguage.En, loc.Resolved);
        Assert.Equal("Light", loc.Get("settings.theme.light"));
        Assert.Equal("5 s", loc.Fmt("interval.seconds", 5));
        Assert.Equal("en-US", loc.LocaleName);
    }

    [Fact]
    public void ExplicitChinese_ReturnsChineseStrings()
    {
        var loc = new Localizer { Language = AppLanguage.ZhCn };

        Assert.Equal(ResolvedLanguage.ZhCn, loc.Resolved);
        Assert.Equal("浅色", loc.Get("settings.theme.light"));
        Assert.Equal("5 秒", loc.Fmt("interval.seconds", 5));
        Assert.Equal("zh-CN", loc.LocaleName);
    }

    [Fact]
    public void FollowSystem_ResolvesToChineseOnlyWhenSystemIsChinese()
    {
        var loc = new Localizer { Language = AppLanguage.FollowSystem };

        var expected = Localizer.SystemUiIsChinese() ? ResolvedLanguage.ZhCn : ResolvedLanguage.En;
        Assert.Equal(expected, loc.Resolved);
        // 不管系统语言是什么,结果一定落在有文案的那两种里。
        Assert.Contains(loc.Resolved, new[] { ResolvedLanguage.ZhCn, ResolvedLanguage.En });
    }

    [Fact]
    public void LanguageDisplayName_MatchesTheResolvedLanguage()
    {
        Assert.Equal("简体中文", new Localizer { Language = AppLanguage.ZhCn }.LanguageDisplayName);
        Assert.Equal("English", new Localizer { Language = AppLanguage.En }.LanguageDisplayName);
    }

    [Fact]
    public void MissingKey_ReturnsTheKeyItself()
    {
        var loc = new Localizer { Language = AppLanguage.En };

        Assert.Equal("this.key.does.not.exist", loc.Get("this.key.does.not.exist"));
        Assert.Equal("", loc.Get(""));
    }

    [Fact]
    public void ChangingLanguage_RaisesEventAndBumpsVersion()
    {
        var loc = new Localizer { Language = AppLanguage.ZhCn };
        var raised = 0;
        loc.LanguageChanged += (_, _) => raised++;
        var versionBefore = loc.Version;

        loc.Language = AppLanguage.En;
        Assert.Equal(1, raised);
        Assert.Equal(versionBefore + 1, loc.Version);

        // 重复选同一个语言不该再惊动界面。
        loc.Language = AppLanguage.En;
        Assert.Equal(1, raised);
        Assert.Equal(versionBefore + 1, loc.Version);
    }

    [Fact]
    public void CoreStrings_ThroughGlobalHelper_FollowThePinnedTestLanguage()
    {
        // TestEnvironment 把 Default 钉成简体中文,老断言的中文原句才不用改。
        Assert.Equal(AppLanguage.ZhCn, Localizer.Default.Language);
        Assert.Equal("5 秒", Loc.Fmt("interval.seconds", 5));
        Assert.Equal("删除", Loc.T("image.remove-label"));
    }

    private static string PlaceholderIndices(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Groups[1].Value));
}