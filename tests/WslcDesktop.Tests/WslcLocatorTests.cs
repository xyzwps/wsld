using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>功能 001 · AC8:wslc.exe 定位顺序。</summary>
public class WslcLocatorTests : IDisposable
{
    [Fact]
    public void Find_EnvironmentVariableOverride_WinsWhenExists()
    {
        var temp = Path.GetTempFileName();
        try
        {
            Environment.SetEnvironmentVariable(WslcLocator.EnvironmentVariable, temp);
            Assert.Equal(temp, WslcLocator.Find());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WslcLocator.EnvironmentVariable, null);
            File.Delete(temp);
        }
    }

    [Fact]
    public void Find_EnvironmentVariableOverride_InvalidPath_ReturnsNull()
    {
        try
        {
            Environment.SetEnvironmentVariable(WslcLocator.EnvironmentVariable, @"Z:\不存在的\wslc.exe");
            Assert.Null(WslcLocator.Find());
        }
        finally
        {
            Environment.SetEnvironmentVariable(WslcLocator.EnvironmentVariable, null);
        }
    }

    [Fact]
    public void Find_OnThisMachine_ResolvesInstalledWslc()
    {
        // 本机已安装 WSL 3 时,应能定位到 Program Files\WSL\wslc.exe。
        var wellKnown = Path.Combine(
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "WSL", "wslc.exe");
        if (!File.Exists(wellKnown)) return; // CI 上未装 wslc 则跳过该断言
        Assert.Equal(wellKnown, WslcLocator.Find());
    }

    [Fact]
    public void NotFoundMessage_MentionsEnvVariable()
    {
        Assert.Contains(WslcLocator.EnvironmentVariable, WslcLocator.NotFoundMessage());
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(WslcLocator.EnvironmentVariable, null);
}