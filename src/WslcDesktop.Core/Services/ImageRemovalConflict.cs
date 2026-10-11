using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 识别"镜像被容器占用,常规删除被拒绝"这一类失败(功能 004 · AC10)。
/// 实测 `wslc rmi` 删不掉正在使用的镜像时,stderr 形如:
///   conflict: unable to remove repository reference "docker.1ms.run/library/busybox:latest" (must force)
///   - container f47fdddf9219 is using its referenced image aaef90e06523
///   错误代码： ERROR_SHARING_VIOLATION
/// 只有命中这类信息,界面才允许把按钮升级为"强制删除";
/// 其它失败(例如 `WSLC_E_IMAGE_NOT_FOUND`)绝不给出破坏性的额外选项。
/// </summary>
public static class ImageRemovalConflict
{
    private static readonly string[] Markers =
    {
        "must force",
        "unable to remove repository reference",
        "ERROR_SHARING_VIOLATION",
    };

    public static bool IsConflict(Exception exception) => exception switch
    {
        WslcCliException cli => Contains(cli.Result.StdErr) || Contains(cli.Result.StdOut),
        _ => false,
    };

    /// <summary>原始文本里是否含有占用冲突的特征片段(单独暴露,便于用真实 stderr 做断言)。</summary>
    public static bool Contains(string? text) =>
        text is not null && Markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
}