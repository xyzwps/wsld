using wsld.Core.Models;
using wsld.Core.Parsing;
using wsld.Core.WslcCli;

namespace wsld.Core.Services;

/// <summary>
/// 读取容器日志的 wslc 调用(功能 005)。
/// 一次性读取走 <see cref="ReadAsync"/>,实时日志走 <see cref="FollowAsync"/>。
/// 两个入口都先用 <see cref="LogQuery.BuildArguments"/> 把参数校验掉:
/// 实测非法行数只会换回 8 行 usage(退出码 1),对使用者毫无价值。
/// </summary>
public sealed class LogsService
{
    /// <summary>出错时给界面用的命令名。</summary>
    public const string CommandName = "wslc logs";

    private readonly IWslcCli _cli;

    public LogsService(IWslcCli cli)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
    }

    /// <summary>
    /// 一次性读取日志(不带 -f)。
    /// 实测:容器已停止或从未启动时是"退出码 0 + 空输出",这里同样返回空快照而不是抛异常。
    /// </summary>
    public async Task<LogSnapshot> ReadAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        var arguments = RequireArguments(query, expectFollow: false);

        var result = await _cli.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new WslcCliException(CommandName, result);
        }

        return new LogSnapshot(WslcLogText.Prepare(result.StdOut, query.Timestamps));
    }

    /// <summary>
    /// 实时日志(带 -f + --since):每读到一行**立刻**回调整理好的文本,直到进程自己结束或被取消。
    /// 取消时抛出 <see cref="OperationCanceledException"/>,已经回调出去的行不会回滚 ——
    /// 界面因此可以保留已显示的日志。
    /// </summary>
    public async Task<LogSnapshot> FollowAsync(
        LogQuery query,
        Action<string> onLine,
        CancellationToken cancellationToken = default)
    {
        var arguments = RequireArguments(query, expectFollow: true);
        if (onLine is null) throw new ArgumentNullException(nameof(onLine));

        var prepared = new List<string>();
        var result = await _cli.RunLinesAsync(
            arguments,
            line =>
            {
                var text = WslcLogText.Pretty(line, query.Timestamps);
                prepared.Add(text);
                onLine(text);
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new WslcCliException(CommandName, result);
        }

        return new LogSnapshot(prepared.ToArray());
    }

    /// <summary>
    /// 参数校验。两种模式与 <see cref="LogQuery.Follow"/> 必须对得上,
    /// 否则会出现"以为在跟随、其实把进程挂住"或反过来的错用。
    /// </summary>
    private static IReadOnlyList<string> RequireArguments(LogQuery query, bool expectFollow)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (query.Follow != expectFollow)
        {
            throw new ArgumentException(
                expectFollow
                    ? "实时日志要求 Follow 为 true。"
                    : "一次性读取要求 Follow 为 false(实时日志请用 FollowAsync)。",
                nameof(query));
        }

        return query.BuildArguments();
    }
}
