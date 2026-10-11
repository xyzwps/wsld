using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Core.Services;

/// <summary>
/// 镜像相关的 wslc 调用。
/// 功能 003:列表查询(功能 004 在此基础上增加拉取与删除)。
/// </summary>
public sealed class ImagesService
{
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
}