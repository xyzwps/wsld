using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>记录调用并按脚本返回结果的假 CLI,用于无进程依赖的单元测试。</summary>
public sealed class FakeWslcCli : IWslcCli
{
    private readonly Queue<WslcCliResult> _scriptedResults = new();
    private WslcCliResult _fallback = new(0, "", "");

    public List<IReadOnlyList<string>> Calls { get; } = new();

    public FakeWslcCli Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _scriptedResults.Enqueue(new WslcCliResult(exitCode, stdout, stderr));
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
}