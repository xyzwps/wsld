using System.Globalization;
using static wsld.Core.I18n.Loc;

namespace wsld.Core.Models;

/// <summary>
/// 一次日志请求(功能 005),两种读法各对应界面一个模式:
/// <see cref="View"/> = 查看日志(一次性读当前能看到的,可带尾部行数);
/// <see cref="Live"/> = 实时日志(**只收这次起点之后的新行,不倒历史**)。
/// 这里同时负责把请求翻译成 wslc 的参数,并在参数非法时**提前**失败 ——
/// 实测 `wslc logs -n 0` / `-n abc` 只会换回 8 行 usage(退出码 1),对使用者毫无价值。
/// </summary>
public sealed record LogQuery
{
    /// <summary>跟随标志(实测 `logs -f` 是流式输出)。</summary>
    public const string FollowFlag = "-f";

    /// <summary>时间戳标志(实测每行前缀 UTC 纳秒 RFC3339)。</summary>
    public const string TimestampsFlag = "-t";

    /// <summary>尾部行数标志。</summary>
    public const string TailFlag = "-n";

    /// <summary>起点标志。实测它同样过滤 `-f` 开头那次历史冲刷,所以"只看新行"靠它。</summary>
    public const string SinceFlag = "--since";

    /// <summary>界面默认只看最近 200 行(不带 -n 时实测会把全部历史冲出来)。</summary>
    public const int DefaultTailLines = 200;

    /// <summary>行数取这个值表示"全部",此时**不发** -n。</summary>
    public const int AllLines = 0;

    /// <summary>行数的上限:再大也只是把无意义的数字发给 CLI(实测超大值本身不报错)。</summary>
    public const int MaxTailLines = 100_000;

    /// <summary>界面可选的行数(顺序即下拉顺序,末尾是"全部")。</summary>
    public static IReadOnlyList<int> TailChoices { get; } = [100, 200, 500, 1_000, 2_000, AllLines];

    /// <summary>容器标识:名称或 ID 都可以,实测三种写法都返回退出码 0。</summary>
    public string ContainerRef { get; init; } = "";

    /// <summary>要看的行数;<see cref="AllLines"/> 表示全部。实时模式不参与拼装。</summary>
    public int TailLines { get; init; } = DefaultTailLines;

    /// <summary>是否要时间戳(-t)。开启后界面把 UTC 纳秒串转成本地时分秒。</summary>
    public bool Timestamps { get; init; }

    /// <summary>是否持续跟随(-f)。</summary>
    public bool Follow { get; init; }

    /// <summary>实时模式的起点(Unix 秒)。跟随必须给,否则 wslc 会先把全部历史冲出来。</summary>
    public long? SinceEpochSeconds { get; init; }

    /// <summary>查看日志:一次性读取,不带 -f。</summary>
    public static LogQuery View(string containerRef, int tailLines, bool timestamps) => new()
    {
        ContainerRef = containerRef,
        TailLines = tailLines,
        Timestamps = timestamps,
        Follow = false,
    };

    /// <summary>
    /// 实时日志:从 <paramref name="sinceEpochSeconds"/> 起跟随,只收新行。
    /// 起点由调用方在**启动这一刻**取(通常是 <c>DateTimeOffset.UtcNow.ToUnixTimeSeconds()</c>)。
    /// </summary>
    public static LogQuery Live(string containerRef, long sinceEpochSeconds, bool timestamps) => new()
    {
        ContainerRef = containerRef,
        TailLines = AllLines,
        Timestamps = timestamps,
        Follow = true,
        SinceEpochSeconds = sinceEpochSeconds,
    };

    /// <summary>行数文案(界面下拉项直接用)。</summary>
    public static string DescribeTail(int lines) =>
        lines == AllLines ? T("log.all-lines") : Fmt("log.tail-lines", lines);

    /// <summary>当前请求的行数文案。</summary>
    public string TailLabel => DescribeTail(TailLines);

    /// <summary>
    /// 拼出 `wslc` 的参数数组:logs [-f] [-t] [--since 起点] [-n 行数] 容器标识,顺序固定。
    /// 实时模式永远不发 -n(它只该看见起点之后的行);任何非法输入都在这里抛出,
    /// <see cref="Services.LogsService"/> 因此永远不需要为它们启动进程。
    /// </summary>
    public IReadOnlyList<string> BuildArguments()
    {
        var reference = (ContainerRef ?? "").Trim();
        if (reference.Length == 0)
        {
            throw new ArgumentException("容器标识不能为空。", nameof(ContainerRef));
        }

        if (Follow)
        {
            if (SinceEpochSeconds is not { } since || since <= 0)
            {
                throw new ArgumentException(
                    "实时日志必须给出起点(Seconds),否则会把全部历史冲出来。",
                    nameof(SinceEpochSeconds));
            }
        }
        else if (TailLines < AllLines || TailLines > MaxTailLines)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.InvariantCulture,
                    "日志行数必须是 {0}(表示全部)到 {1} 之间的整数。", AllLines, MaxTailLines),
                nameof(TailLines));
        }

        var arguments = new List<string> { "logs" };
        if (Follow) arguments.Add(FollowFlag);
        if (Timestamps) arguments.Add(TimestampsFlag);
        if (SinceEpochSeconds is { } value)
        {
            arguments.Add(SinceFlag);
            arguments.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        if (!Follow && TailLines != AllLines)
        {
            arguments.Add(TailFlag);
            arguments.Add(TailLines.ToString(CultureInfo.InvariantCulture));
        }

        arguments.Add(reference);
        return arguments;
    }
}
