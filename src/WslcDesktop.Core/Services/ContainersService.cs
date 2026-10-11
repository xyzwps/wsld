using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Core.Services;

/// <summary>容器列表查询(功能 001)。生命周期操作将在功能 002 加入本服务。</summary>
public sealed class ContainersService
{
    private readonly IWslcCli _cli;

    public ContainersService(IWslcCli cli)
    {
        _cli = cli;
    }

    /// <summary>列出全部容器(含已停止),对应 `wslc list -a --format json`。</summary>
    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = await _cli.RunAsync(new[] { "list", "-a", "--format", "json" }, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException("wslc list", result);
        }
        return WslcJsonParser.ParseContainers(result.StdOut);
    }
}