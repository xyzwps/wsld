using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Services;

/// <summary>
/// 极简组合根:全局只创建一次 wslc 客户端与各服务实例,页面从这里取用。
/// wslc.exe 定位失败时降级为 UnavailableWslcCli,让界面以错误条呈现(功能 001 · AC8)。
/// </summary>
public static class AppServices
{
    public static IWslcCli WslcCli { get; } = CreateCli();

    public static ContainersService Containers { get; } = new(WslcCli);
    public static ImagesService Images { get; } = new(WslcCli);
    public static LogsService Logs { get; } = new(WslcCli);

    /// <summary>就地执行历史(功能 012 补强):全局一份,切语言重建命令编排页时历史不丢。</summary>
    public static RunHistory RunHistory { get; } = new();

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