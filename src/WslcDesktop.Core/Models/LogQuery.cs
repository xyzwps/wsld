using System.Globalization;
using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.Models;

/// <summary>
/// 一次日志请求(功能 005 · AC1/AC2)。
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

    /// <summary>界面默认只看最近 200 行(不带 -n 时实测会把全部历史冲出来)。</summary>
    public const int DefaultTailLines = 200;

    /// <summary>行数取这个值表示"全部",此时**不发** -n。</summary>
    public const int AllLines = 0;

    /// <summary>行数的上限:再大也只是把无意义的数字发给 CLI(实测超大值本身不报错)。</summary>
    public const int MaxTailLines = 100_000;

    /// <summary>界面可选的行数(顺序即下拉顺序,末尾是"全部")。</summary>
    public static IReadOnlyList<int> TailChoices { get; } = [100, 200, 500, 1_000, 2_000, AllLines];

    public static string AllLinesLabel => T("log.all-lines");

    /// <summary>容器标识:名称或 ID 都可以,实测三种写法都返回退出码 0。</summary>
    public string ContainerRef { get; init; } = "";

    /// <summary>要看的行数;<see cref="AllLines"/> 表示全部。</summary>
    public int TailLines { get; init; } = DefaultTailLines;

    /// <summary>是否要时间戳(-t)。开启后界面把 UTC 纳秒串转成本地时分秒(AC5)。</summary>
    public bool Timestamps { get; init; }

    /// <summary>是否持续跟随(-f)。</summary>
    public bool Follow { get; init; }

    /// <summary>行数为 0 时表示"全部",界面文案。</summary>
    public static string DescribeTail(int lines) =>
        lines == AllLines ? AllLinesLabel : Fmt("log.tail-lines", lines);

    /// <summary>当前请求的行数文案。</summary>
    public string TailLabel => DescribeTail(TailLines);

    /// <summary>
    /// 拼出 `wslc` 的参数数组:logs [-f] [-t] [-n 行数] 容器标识,顺序固定(AC1)。
    /// 任何非法输入都在这里抛出,<see cref="Services.LogsService"/> 因此永远不需要为它们启动进程(AC2)。
    /// </summary>
    public IReadOnlyList<string> BuildArguments()
    {
        var reference = (ContainerRef ?? "").Trim();
        if (reference.Length == 0)
        {
            throw new ArgumentException("容器标识不能为空。", nameof(ContainerRef));
        }

        if (TailLines < AllLines || TailLines > MaxTailLines)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.InvariantCulture,
                    "日志行数必须是 {0}(表示全部)到 {1} 之间的整数。", AllLines, MaxTailLines),
                nameof(TailLines));
        }

        var arguments = new List<string> { "logs" };
        if (Follow) arguments.Add(FollowFlag);
        if (Timestamps) arguments.Add(TimestampsFlag);
        if (TailLines != AllLines)
        {
            arguments.Add(TailFlag);
            arguments.Add(TailLines.ToString(CultureInfo.InvariantCulture));
        }
        arguments.Add(reference);
        return arguments;
    }
}