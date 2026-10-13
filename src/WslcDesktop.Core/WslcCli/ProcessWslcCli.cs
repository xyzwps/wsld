using System.Diagnostics;
using System.Text;
using WslcDesktop.Core.Parsing;

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

    /// <summary>伪终端的宽高。宽度按常见终端取;高度要能容下一次拉取的全部分层行(AC1)。</summary>
    private const int ConsoleWidth = 120;
    private const int ConsoleHeight = 40;

    /// <summary>子进程退出后留给读线程的收尾时间:最后一帧可能还堵在管道里。</summary>
    private const int FlushGraceMs = 250;

    /// <summary>拆掉伪控制台之后最多再等读线程这么久,超时就不再要这份输出(兜底,正常到不了)。</summary>
    private const int ReaderStopMs = 2_000;

    /// <summary>
    /// 伪终端路径(功能 011 · AC1/AC3/AC5)。三步:起会话 → 后台读"屏幕"并回调 → 等进程退出后收尾。
    /// 读循环不等在 <c>ReadAsync</c> 上的取消,而是靠"杀掉子进程 + 拆掉伪控制台"让管道自然断流,
    /// 这样不会出现"取消时正好把已经到达的字节丢掉"的情况。
    /// </summary>
    public async Task<WslcCliResult> RunWithConsoleAsync(
        IReadOnlyList<string> arguments,
        Action<IReadOnlyList<string>>? onVisibleLines,
        CancellationToken cancellationToken = default)
    {
        if (!ConPtySession.TryStart(_exePath, arguments, ConsoleWidth, ConsoleHeight, out var session, out _))
        {
            // 伪终端起不来(理论上只有 1809 之前的 Windows,或安全软件拦了 conhost)。
            // 退回普通管道:结果一模一样,只是中途拿不到实时进度,结束时一次性把全部行交出去(AC5)。
            var plain = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
            Notify(onVisibleLines, WslcLogText.Split(plain.StdOut));
            return plain;
        }

        using (session)
        using (cancellationToken.Register(static state => ((ConPtySession)state!).KillTree(), session))
        {
            var tracker = new VtScreenTracker();
            var reading = ReadScreenAsync(session!.Output, tracker, onVisibleLines);
            IReadOnlyList<string> visible;
            try
            {
                await WaitForChildExitAsync(session, cancellationToken).ConfigureAwait(false);
                // 退出≠屏幕最后一帧已经读完,给它一点时间冲出来。
                await Task.Delay(FlushGraceMs, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                // 取消/异常同样要拆控制台:写端全关,读线程才拿得到 EOF,不留一个挂死的后台读。
                session.CloseConsole();
                visible = await DrainAsync(reading).ConfigureAwait(false);
            }

            // 与 RunAsync/RunLinesAsync 同一条约定:取消就必须算取消,不能被报成"命令失败"。
            cancellationToken.ThrowIfCancellationRequested();

            var screen = string.Join(LineBreak, visible);
            // 控制台把 stdout 与 stderr 合成一路,分不开:整块屏幕当 stdout(Status 行在其中,解析照旧);
            // 错误横幅只要"不是分层进度"的那些行,否则几十行 a60ef8ec184e: Downloading… 会把真正的报错淹没。
            var errorText = string.Join(LineBreak, visible.Where(line => !WslcPullProgress.IsLayerLine(line)));
            return new WslcCliResult(session.ExitCode ?? 0, screen, errorText.Length > 0 ? errorText : screen);
        }
    }

    /// <summary>
    /// 读线程:字节 → UTF-8(一个汉字可能被拆在两个 chunk 里,所以用 <see cref="Decoder"/> 续解)
    /// → VT 屏幕模型 → 屏幕有变化才回调(每帧都回调会让界面白抖)。
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadScreenAsync(
        Stream output,
        VtScreenTracker tracker,
        Action<IReadOnlyList<string>>? onVisibleLines)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192];
        try
        {
            while (true)
            {
                var read = await output.ReadAsync(bytes.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                if (read == 0) break; // 写端全部关闭 = 子进程已经收场

                var count = decoder.GetChars(bytes, 0, read, chars, 0);
                if (count == 0) continue;
                if (tracker.Feed(new string(chars, 0, count)))
                {
                    Notify(onVisibleLines, tracker.Snapshot());
                }
            }
        }
        catch (Exception)
        {
            // 伪控制台被拆、管道断裂、流已释放:都只意味着"没有更多可读了",不是新错误。
        }

        return tracker.Snapshot();
    }

    /// <summary>等子进程结束。真异步等待,不占线程;取消由 <c>KillTree</c> 把进程带走,这里的等待随之结束。</summary>
    private static async Task WaitForChildExitAsync(ConPtySession session, CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.GetProcessById(session.ProcessId);
        }
        catch (ArgumentException)
        {
            return; // 命令瞬间结束(例如参数就写错了),已经退场
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>收尾拿屏幕行,但最多等 <see cref="ReaderStopMs"/>;等不到就放弃这份输出。</summary>
    private static async Task<IReadOnlyList<string>> DrainAsync(Task<IReadOnlyList<string>> reading)
    {
        try
        {
            var finished = await Task.WhenAny(reading, Task.Delay(ReaderStopMs, CancellationToken.None))
                .ConfigureAwait(false);
            if (finished == reading) return await reading.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读线程因中断抛出的异常在这里没有意义,取消路径随后仍会抛 OperationCanceledException。
        }

        return Array.Empty<string>();
    }

    /// <summary>回调是给界面用的,坏了不能拖累读线程 —— 异常一律吞掉。</summary>
    private static void Notify(Action<IReadOnlyList<string>>? onVisibleLines, IReadOnlyList<string> lines)
    {
        if (onVisibleLines is null) return;
        try
        {
            onVisibleLines(lines);
        }
        catch (Exception)
        {
            // 忽略:界面的刷新失败不该让一次拉取变成"命令执行失败"。
        }
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