using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Services;

/// <summary>
/// 极简组合根:全局只创建一次 wslc 客户端与各服务实例,页面从这里取用。
/// wslc.exe 定位失败时降级为 UnavailableWslcCli,让界面以错误条呈现(功能 001 · AC8)。
/// </summary>
public static class AppServices
{
    public static IWslcCli WslcCli { get; } = CreateCli();

    public static ContainersService Containers { get; } = new(WslcCli);

    /// <summary>wslc.exe 的完整路径,用于状态栏展示;未找到时为 null。</summary>
    public static string? WslcPath => WslcLocator.Find();

    private static IWslcCli CreateCli()
    {
        try
        {
            return new ProcessWslcCli();
        }
        catch (InvalidOperationException ex)
        {
            return new UnavailableWslcCli(ex.Message);
        }
    }
}