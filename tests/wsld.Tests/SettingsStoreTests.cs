using wsld.Core.I18n;
using wsld.Core.Settings;

namespace wsld.Tests;

/// <summary>
/// 册子 SET-004–SET-008、SET-012–SET-016(并及 TH-011、TH-012、LANG-004):
/// 设置文件的读写与容错。
/// 全部走临时目录里的具体路径(而不是默认的 %LOCALAPPDATA% 位置),
/// 测试机器上真实的用户设置不能被单测改掉。
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wslc-settings-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    [Trait("case", "TH-011")]
    [Trait("case", "LANG-004")]
    public void RoundTrip_KeepsThemeAndLanguage()
    {
        var saved = SettingsStore.SaveTo(new AppSettings { Theme = ThemeMode.Light, Language = AppLanguage.En }, SettingsPath);

        Assert.True(saved);
        var loaded = SettingsStore.LoadFrom(SettingsPath);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.Equal(AppLanguage.En, loaded.Language);
    }

    [Fact]
    [Trait("case", "SET-017")]
    public void RoundTrip_CreatesMissingDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");

        Assert.True(SettingsStore.SaveTo(new AppSettings { Theme = ThemeMode.Dark }, nested));
        Assert.Equal(ThemeMode.Dark, SettingsStore.LoadFrom(nested).Theme);
    }

    [Fact]
    [Trait("case", "SET-004")]
    public void MissingFile_FallsBackToDefaults()
    {
        var loaded = SettingsStore.LoadFrom(Path.Combine(_dir, "nope.json"));

        Assert.Equal(ThemeMode.FollowSystem, loaded.Theme);
        Assert.Equal(AppLanguage.FollowSystem, loaded.Language);
    }

    [Fact]
    [Trait("case", "SET-005")]
    public void CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json ");

        Assert.Equal(ThemeMode.FollowSystem, SettingsStore.LoadFrom(SettingsPath).Theme);
    }

    [Fact]
    [Trait("case", "SET-013")]
    [Trait("case", "TH-012")]
    public void SavedFile_UsesReadableEnumNames()
    {
        SettingsStore.SaveTo(new AppSettings { Theme = ThemeMode.Dark, Language = AppLanguage.ZhCn }, SettingsPath);
        var text = File.ReadAllText(SettingsPath);

        Assert.Contains("\"Dark\"", text);
        Assert.Contains("\"ZhCn\"", text);
    }

    [Fact]
    [Trait("case", "SET-006")]
    public void HandWrittenJson_IsTolerated()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
        {
          // 有人手改过,带注释也应该能读
          "theme": "Light",
          "language": "En",
        }
        """);

        var loaded = SettingsStore.LoadFrom(SettingsPath);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.Equal(AppLanguage.En, loaded.Language);
    }

    [Fact]
    [Trait("case", "SET-006")]
    public void Utf8BomPrefixedFile_IsStillRead()
    {
        // 记事本 / Visual Studio 常把文件存成"带 BOM 的 UTF-8",开头多出 EF BB BF。
        // 解析器会把它当非法字符,异常被吞掉后就变成"设置莫名其妙被重置成跟随系统"(开发笔记 #22)。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{"Theme":"Dark","Language":"En"}""", new System.Text.UTF8Encoding(true));

        var loaded = SettingsStore.LoadFrom(SettingsPath);
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal(AppLanguage.En, loaded.Language);
    }

    [Fact]
    [Trait("case", "SET-008")]
    public void UnknownFutureField_IsIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{"Theme":"Light","Experimental":["keep me"]}""");

        Assert.Equal(ThemeMode.Light, SettingsStore.LoadFrom(SettingsPath).Theme);
    }

    [Fact]
    [Trait("case", "SET-007")]
    public void WritingToAnImpossibleSettingsPathReportsFailureInsteadOfThrowing()
    {
        var bogus = Path.Combine(_dir, "..", "\0invalid", "settings.json");

        Assert.False(SettingsStore.SaveTo(new AppSettings(), bogus));
    }

    [Fact]
    [Trait("case", "SET-012")]
    public void DefaultLocation_LivesUnderLocalAppData()
    {
        var expectedRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 目录拿不到时退回临时目录,两种都是合理位置,但不能是个相对路径。
        Assert.True(Path.IsPathRooted(SettingsStore.FilePath));
        Assert.EndsWith(Path.Combine("wsld", "settings.json"), SettingsStore.FilePath);
        Assert.StartsWith(string.IsNullOrEmpty(expectedRoot) ? Path.GetTempPath() : expectedRoot, SettingsStore.FilePath);
    }

    // ---------- 册子 SET-014–SET-016 · 镜像源字段 ----------

    [Fact]
    [Trait("case", "SET-018")]
    [Trait("case", "SET-016")]
    [Trait("case", "RM-005")]
    public void RoundTrip_KeepsRegistryMirrorAndRenameOption()
    {
        var saved = SettingsStore.SaveTo(
            new AppSettings { RegistryMirror = "docker.1ms.run", RenameMirrorPull = false }, SettingsPath);

        Assert.True(saved);
        var loaded = SettingsStore.LoadFrom(SettingsPath);
        Assert.Equal("docker.1ms.run", loaded.RegistryMirror);
        Assert.False(loaded.RenameMirrorPull);
    }

    [Fact]
    [Trait("case", "SET-014")]
    public void OldFile_WithoutMirrorFields_KeepsWorkingWithDefaults()
    {
        // 009 时代存的文件只有 Theme / Language,新增字段必须有默认值(开发笔记 #9)。
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{"Theme":"Light","Language":"En"}""");

        var loaded = SettingsStore.LoadFrom(SettingsPath);
        Assert.Equal("", loaded.RegistryMirror);   // 默认不使用镜像源
        Assert.True(loaded.RenameMirrorPull);      // 默认帮用户把名字改回来
    }

    [Fact]
    [Trait("case", "SET-015")]
    public void NullMirrorInFile_DoesNotBecomeNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{"RegistryMirror":null}""");

        Assert.Equal("", SettingsStore.LoadFrom(SettingsPath).RegistryMirror);
    }

    [Fact]
    [Trait("case", "SET-015")]
    public void AssigningNullMirror_FallsBackToEmptyString()
    {
        var settings = new AppSettings { RegistryMirror = "docker.1ms.run" };
        settings.RegistryMirror = null!;

        Assert.Equal("", settings.RegistryMirror);
    }

    [Fact]
    [Trait("case", "SET-016")]
    public void SavedFile_NamesTheMirrorFieldsReadable()
    {
        SettingsStore.SaveTo(new AppSettings { RegistryMirror = "localhost:5000/mirror" }, SettingsPath);
        var text = File.ReadAllText(SettingsPath);

        // 存的是用户写的那串原样(规范化在使用侧做),手改文件时也一眼能认出来。
        Assert.Contains("\"RegistryMirror\": \"localhost:5000/mirror\"", text);
        Assert.Contains("\"RenameMirrorPull\": true", text);
    }
}
