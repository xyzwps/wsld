using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>记录调用并按脚本返回结果的假 CLI,用于无进程依赖的单元测试。</summary>
public sealed class FakeWslcCli : IWslcCli
{
    private readonly Queue<WslcCliResult> _scriptedResults = new();
    private readonly Queue<(int ExitCode, IReadOnlyList<string> Lines, string StdErr)> _scriptedStreams = new();
    private WslcCliResult _fallback = new(0, "", "");

    public List<IReadOnlyList<string>> Calls { get; } = new();

    /// <summary>逐行回调的接管者(功能 005 · AC7/AC8 用它制造"进程还在流"的场景)。</summary>
    public Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<WslcCliResult>>? StreamHandler { get; set; }

    public List<IReadOnlyList<string>> StreamCalls { get; } = new();

    public FakeWslcCli Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _scriptedResults.Enqueue(new WslcCliResult(exitCode, stdout, stderr));
        return this;
    }

    /// <summary>排一个"边读边回调"的脚本:stdout 按实测的 LF 拆成行,每行都会立刻交给回调(AC7)。</summary>
    public FakeWslcCli EnqueueStream(int exitCode, string stdout, string stderr = "")
    {
        _scriptedStreams.Enqueue((exitCode, WslcLogText.Split(stdout), stderr));
        return this;
    }

    public FakeWslcCli AlwaysReturn(int exitCode, string stdout = "", string stderr = "")
    {
        _fallback = new WslcCliResult(exitCode, stdout, stderr);
        return this;
    }

    public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments);
        var result = _scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : _fallback;
        return Task.FromResult(result);
    }

    public Task<WslcCliResult> RunLinesAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments);
        StreamCalls.Add(arguments);

        if (StreamHandler is not null)
        {
            return StreamHandler(arguments, onStdoutLine, cancellationToken);
        }

        if (_scriptedStreams.Count > 0)
        {
            var (exitCode, lines, stderr) = _scriptedStreams.Dequeue();
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onStdoutLine?.Invoke(line);
            }

            return Task.FromResult(new WslcCliResult(exitCode, string.Join("\n", lines), stderr));
        }

        // 没排流式脚本时退化成一次性结果,行为与 RunAsync 一致。
        var result = _scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : _fallback;
        foreach (var line in WslcLogText.Split(result.StdOut))
        {
            onStdoutLine?.Invoke(line);
        }
        return Task.FromResult(result);
    }
}