namespace WslcDesktop.Core.WslcCli;

/// <summary>wslc 命令行的一次调用结果。</summary>
public sealed record WslcCliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>wslc CLI 的进程调用抽象。测试时可注入 Fake 实现。</summary>
public interface IWslcCli
{
    /// <summary>启动进程,等它结束,一次拿到全部 stdout / stderr(功能 001–004 的唯一路径)。</summary>
    Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    /// <summary>
    /// 同上,但 stdout 按行**边读边回调**(功能 005 · AC7)。
    /// 实测 <c>wslc logs -f</c> 是真流式:每 2 秒稳定多出一行,不用等进程结束,
    /// 所以"实时跟随"做得出来(对比 <c>wslc pull</c>:它的输出要到进程退出才一次性冲刷,做不了)。
    /// 回调在后台读线程上执行,调用方自己负责回到 UI 线程。<see cref="IWslcCli"/> 的其它路径不受影响(AC17)。
    /// </summary>
    Task<WslcCliResult> RunLinesAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在伪终端(ConPTY)里运行,并把"屏幕上此刻可见的行"**边渲染边回调**(功能 011 · AC1/AC3)。
    /// 和 <see cref="RunLinesAsync"/> 的区别在于子进程面对的输出口:
    /// 实测普通重定向管道下 <c>wslc pull</c> 的 45 行进度要等进程退出才一次性冲刷(功能 004 的实测表),
    /// 而把它挂进伪终端后 wslc 认为自己在真终端上,每 ~20 秒就把最新一帧写出来,实时进度因此做得出来。
    /// 管道那两条路径不受影响(AC17 式的"不破坏旧约定")。
    /// 伪终端创建失败时自动退回普通管道,结束时一次性回调全部行,调用方不用管这个分支(AC5)。
    /// 回调在后台读线程上执行,调用方自己负责回 UI 线程;回调里抛的异常会被吞掉,绝不能打断进程本身。
    /// 返回结果里 stdout 与 stderr 都来自同一块"屏幕"——控制台把两路输出合成一路,分不开。
    /// </summary>
    Task<WslcCliResult> RunWithConsoleAsync(
        IReadOnlyList<string> arguments,
        Action<IReadOnlyList<string>>? onVisibleLines,
        CancellationToken cancellationToken = default);
}