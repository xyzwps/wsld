namespace WslcDesktop.Core.WslcCli;

/// <summary>
/// 把 wslc 的 stderr 清洗成适合放在界面横幅上的一句话(功能 002 · AC4)。
/// wslc 失败时会在真正的错误后面追加一句固定的"欢迎提 issue"样板提示,
/// 对使用者没有价值,展示时去掉。
/// </summary>
public static class WslcError
{
    /// <summary>样板提示的识别片段。</summary>
    public const string BoilerplateMarker = "如果此错误是意外错误";

    public const string FallbackMessage = "wslc 命令执行失败。";

    public static string Describe(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return FallbackMessage;

        var lines = stderr
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0 && !line.Contains(BoilerplateMarker, StringComparison.Ordinal))
            .ToList();

        return lines.Count == 0 ? FallbackMessage : string.Join(' ', lines);
    }

    /// <summary>任何 wslc 相关异常 → 界面文案。</summary>
    public static string Describe(Exception exception) => exception switch
    {
        WslcCliException cliError => Describe(cliError.Result.StdErr),
        _ => exception.Message,
    };
}