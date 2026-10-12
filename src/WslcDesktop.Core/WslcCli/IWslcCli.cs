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
}