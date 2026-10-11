using System.Diagnostics;
using System.Text;

namespace WslcDesktop.Core.WslcCli;

/// <summary>真实进程实现：启动 wslc.exe 并捕获 stdout/stderr。</summary>
public sealed class ProcessWslcCli : IWslcCli
{
    private readonly string _exePath;

    /// <summary>解析不到 wslc.exe 时抛出 <see cref="InvalidOperationException"/>（消息为 <see cref="WslcLocator.NotFoundMessage"/>）。</summary>
    public ProcessWslcCli(string? exePath = null)
    {
        _exePath = exePath ?? WslcLocator.Find() ?? throw new InvalidOperationException(WslcLocator.NotFoundMessage());
    }

    public async Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
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

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var killOnCancel = cancellationToken.Register(static state =>
        {
            var p = (Process)state!;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* 进程可能已退出 */ }
        }, process);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new WslcCliResult(process.ExitCode, stdout, stderr);
    }
}