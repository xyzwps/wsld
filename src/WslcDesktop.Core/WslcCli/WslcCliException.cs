namespace WslcDesktop.Core.WslcCli;

/// <summary>wslc 返回非零退出码时抛出（功能 001 · AC9：错误文案包含 stderr）。</summary>
public sealed class WslcCliException : Exception
{
    public WslcCliResult Result { get; }

    public WslcCliException(string command, WslcCliResult result)
        : base(BuildMessage(command, result))
    {
        Command = command;
        Result = result;
    }

    public string Command { get; }

    private static string BuildMessage(string command, WslcCliResult result)
    {
        var detail = FirstNonEmpty(result.StdErr, result.StdOut);
        return detail is null
            ? $"{command} 执行失败（退出码 {result.ExitCode}）。"
            : $"{command} 执行失败（退出码 {result.ExitCode}）：{detail}";
    }

    private static string? FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }
}