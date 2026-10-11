namespace WslcDesktop.Core.WslcCli;

/// <summary>wslc 命令行的一次调用结果。</summary>
public sealed record WslcCliResult(int ExitCode, string StdOut, string StdErr);

/// <summary>wslc CLI 的进程调用抽象。测试时可注入 Fake 实现。</summary>
public interface IWslcCli
{
    Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}