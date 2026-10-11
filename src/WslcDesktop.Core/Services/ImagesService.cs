using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 镜像相关的 wslc 调用。
/// 功能 003:列表查询;功能 004:拉取与删除。
/// </summary>
public sealed class ImagesService
{
    /// <summary>强制删除参数(功能 004 · 实测只有 -f 能删掉被容器引用的镜像)。</summary>
    public const string ForceFlag = "-f";

    private readonly IWslcCli _cli;

    public ImagesService(IWslcCli cli)
    {
        _cli = cli;
    }

    /// <summary>列出本机镜像,对应 `wslc images --format json`(功能 003 · AC1)。</summary>
    public async Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _cli.RunAsync(new[] { "images", "--format", "json" }, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc images", result);
        }
        return WslcJsonParser.ParseImages(result.StdOut);
    }

    /// <summary>
    /// 拉取镜像,对应 `wslc pull &lt;引用&gt;`(功能 004 · AC1)。
    /// 不用 `-q`:实测详细输出的 `Status:` 行能区分"新下载"和"已是最新"。
    /// </summary>
    public async Task<PullResult> PullAsync(string reference, CancellationToken cancellationToken = default)
    {
        var target = RequireReference(reference);
        var result = await _cli.RunAsync(new[] { "pull", target }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc pull", result);
        }
        return WslcPullLog.Parse(target, result.StdOut);
    }

    /// <summary>
    /// 删除镜像引用,对应 `wslc rmi [-f] &lt;引用&gt;`(功能 004 · AC4)。
    /// 实测 `rmi` 一次传多个引用时遇到第一个失败就停手,所以这里坚持"一次只删一个引用"。
    /// </summary>
    public async Task RemoveAsync(string reference, bool force = false, CancellationToken cancellationToken = default)
    {
        var target = RequireReference(reference);
        var arguments = force
            ? new string[] { "rmi", ForceFlag, target }
            : new string[] { "rmi", target };

        var result = await _cli.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc rmi", result);
        }
    }

    /// <summary>
    /// 空引用属于不可挽回操作的输入错误,必须在启动进程之前挡掉(功能 004 · AC2)。
    /// </summary>
    private static string RequireReference(string? reference)
    {
        var trimmed = reference?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("镜像引用不能为空。", nameof(reference));
        }
        return trimmed;
    }
}
