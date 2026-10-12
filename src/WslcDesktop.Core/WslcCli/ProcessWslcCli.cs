using System.Diagnostics;
using System.Text;

namespace WslcDesktop.Core.WslcCli;

/// <summary>真实进程实现:启动 wslc.exe 并捕获 stdout/stderr。</summary>
public sealed class ProcessWslcCli : IWslcCli
{
    /// <summary>wslc 的日志行分隔符(实测行尾只有 LF)。</summary>
    public const string LineBreak = "\n";

    private readonly string _exePath;

    /// <summary>解析不到 wslc.exe 时抛出 <see cref="InvalidOperationException"/>（消息为 <see cref="WslcLocator.NotFoundMessage"/>）。</summary>
    public ProcessWslcCli(string? exePath = null)
    {
        _exePath = exePath ?? WslcLocator.Find() ?? throw new InvalidOperationException(WslcLocator.NotFoundMessage());
    }

    public async Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        using var process = StartProcess(arguments);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var killOnCancel = cancellationToken.Register(static state => KillTree((Process)state!), process);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        // 实测:取消触发的整树终止后,wslc 会带着退出码 -1 自己退场,
        // "进程退出"与"取消"存在竞态。取消就必须算取消,不能被报成"命令失败"。
        cancellationToken.ThrowIfCancellationRequested();
        return new WslcCliResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// 逐行回调(功能 005 · AC7)。用 <c>ReadLineAsync</c> 循环而不是 <c>ReadToEndAsync</c>,
    /// 这样 <c>logs -f</c> 每产生一行就能马上显示;返回结果里仍然带完整的 stdout,
    /// 便于非跟随模式下按同一套逻辑解析。
    /// </summary>
    public async Task<WslcCliResult> RunLinesAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken = default)
    {
        using var process = StartProcess(arguments);

        var stderrTask = process.StandardError.ReadToEndAsync();
        using var killOnCancel = cancellationToken.Register(static state => KillTree((Process)state!), process);

        var lines = new List<string>();
        try
        {
            var reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lines.Add(line);
                onStdoutLine?.Invoke(line);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // 实测:取消杀掉 wslc 后它往往以退出码 -1 自己退场,WaitForExit 与取消的先后是竞态。
            // 令牌已经触发就一律按取消处理 —— "停止跟随"永远不该变成一条红色错误(AC8)。
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            // 取消、行回调抛异常、流被中断 —— 三种情况都必须先把子进程带走,
            // 否则后台会留一个还在不停产生日志的 wslc(实测整树终止后无残留)。
            KillTree(process);
            _ = stderrTask.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

            // 取消导致的管道断裂/读流异常也归一成 OperationCanceledException。
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        return new WslcCliResult(process.ExitCode, string.Join(LineBreak, lines), stderr);
    }

    private Process StartProcess(IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi };
        process.Start();
        return process;
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出
        }
    }
}