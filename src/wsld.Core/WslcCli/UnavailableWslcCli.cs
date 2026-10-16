namespace wsld.Core.WslcCli;

/// <summary>wslc.exe 定位失败。</summary>
public sealed class WslcCliUnavailableException : Exception
{
    public WslcCliUnavailableException(string message) : base(message) { }
}

/// <summary>wslc.exe 不可用时的占位实现：任何调用都返回友好错误,让 UI 以错误条形式展示而不是启动崩溃(功能 001 · AC8)。</summary>
public sealed class UnavailableWslcCli : IWslcCli
{
    private readonly string _message;

    public UnavailableWslcCli(string? message = null)
    {
        _message = message ?? WslcLocator.NotFoundMessage();
    }

    public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        => Task.FromException<WslcCliResult>(new WslcCliUnavailableException(_message));

    public Task<WslcCliResult> RunLinesAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken = default)
        => Task.FromException<WslcCliResult>(new WslcCliUnavailableException(_message));

    public Task<WslcCliResult> RunWithConsoleAsync(
        IReadOnlyList<string> arguments,
        Action<IReadOnlyList<string>>? onVisibleLines,
        CancellationToken cancellationToken = default)
        => Task.FromException<WslcCliResult>(new WslcCliUnavailableException(_message));
}