using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.WslcCli;


namespace WslcDesktop.Core.Services;

/// <summary>
/// 容器相关的 wslc 调用。
/// 功能 001:列表查询;功能 002:启动/停止/重启/删除。
/// </summary>
public sealed class ContainersService
{
    /// <summary>删除运行中的容器所需的强制参数(功能 002 · 实测 WSLC_E_CONTAINER_IS_RUNNING)。</summary>
    public const string ForceFlag = "-f";

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

    /// <summary>启动容器。对已运行的容器执行同样返回成功(wslc 幂等)。</summary>
    public Task StartAsync(string id, CancellationToken cancellationToken = default) =>
        RunGuardedAsync("start", new[] { "start", id }, cancellationToken);

    /// <summary>停止容器。对已停止的容器执行同样返回成功(wslc 幂等)。</summary>
    public Task StopAsync(string id, CancellationToken cancellationToken = default) =>
        RunGuardedAsync("stop", new[] { "stop", id }, cancellationToken);

    /// <summary>重启容器;未运行的容器会被直接启动。</summary>
    public Task RestartAsync(string id, CancellationToken cancellationToken = default) =>
        RunGuardedAsync("restart", new[] { "restart", id }, cancellationToken);

    /// <summary>删除容器;运行中的容器必须 <paramref name="force"/> = true。</summary>
    public Task RemoveAsync(string id, bool force = false, CancellationToken cancellationToken = default) =>
        RunGuardedAsync(
            "remove",
            force ? new string[] { "remove", ForceFlag, id } : new[] { "remove", id },
            cancellationToken);

    /// <summary>按枚举分派,供 ViewModel 统一调用。</summary>
    public Task RunAsync(ContainerAction action, string id, bool force = false, CancellationToken cancellationToken = default) =>
        action switch
        {
            ContainerAction.Start => StartAsync(id, cancellationToken),
            ContainerAction.Stop => StopAsync(id, cancellationToken),
            ContainerAction.Restart => RestartAsync(id, cancellationToken),
            ContainerAction.Remove => RemoveAsync(id, force, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的容器操作。"),
        };

    /// <summary>
    /// 就地执行 wslc run(功能 012 · AC7)。参数由 <see cref="RunCommandBuilder"/> 生成。
    /// stdout 逐行回调:后台模式进程秒回(附容器 ID);前台模式容器输出会像 `logs -f` 一样
    /// 一行一行流回来,直到容器退出 —— 实测依据见功能 005(逐行流式在 wslc 里是可行的)。
    /// 这里不抛异常:结果原样交给命令编排页,由它把退出码/stderr 摆进输出面板。
    /// </summary>
    public Task<WslcCliResult> RunAsync(RunSpec spec, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
        _cli.RunLinesAsync(RunCommandBuilder.Build(spec), onStdoutLine, cancellationToken);

    private async Task RunGuardedAsync(string verb, string[] arguments, CancellationToken cancellationToken)
    {
        var id = arguments[^1];
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("容器 ID 不能为空。", nameof(id));
        }

        var result = await _cli.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException($"wslc {verb}", result);
        }
    }
}