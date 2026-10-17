using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>册子 CL-021、CL-022:wslc.exe 的定位顺序与找不到时的提示。</summary>
public class WslcLocatorTests : IDisposable
{
    [Fact]
    [Trait("case", "CL-022")]
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
    [Trait("case", "CL-023")]
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
    [Trait("case", "CL-022")]
    public void Find_OnThisMachine_ResolvesInstalledWslc()
    {
        // 本机已安装 WSL 3 时,应能定位到 Program Files\WSL\wslc.exe。
        var wellKnown = Path.Combine(
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "WSL", "wslc.exe");
        if (!File.Exists(wellKnown)) return; // CI 上未装 wslc 则跳过该断言
        Assert.Equal(wellKnown, WslcLocator.Find());
    }

    [Fact]
    [Trait("case", "CL-021")]
    public void NotFoundMessage_MentionsEnvVariable()
    {
        Assert.Contains(WslcLocator.EnvironmentVariable, WslcLocator.NotFoundMessage());
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(WslcLocator.EnvironmentVariable, null);
}
