using static WslcDesktop.Core.I18n.Loc;

namespace WslcDesktop.Core.WslcCli;

/// <summary>
/// 定位 wslc.exe（功能 001 · AC8）：
/// 环境变量 WSLC_DESKTOP_WSLC_PATH > %ProgramFiles%\WSL\wslc.exe > PATH。
/// </summary>
public static class WslcLocator
{
    public const string EnvironmentVariable = "WSLC_DESKTOP_WSLC_PATH";
    private const string ExeName = "wslc.exe";

    public static string? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return File.Exists(fromEnv) ? fromEnv : null;
        }

        foreach (var candidate in DefaultLocations())
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>找不到 wslc.exe 时的友好提示（写入异常消息，供 UI 展示）。</summary>
    public static string NotFoundMessage() => Fmt("wslc.not-found", EnvironmentVariable);

    private static IEnumerable<string> DefaultLocations()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "WSL", ExeName);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), ExeName); }
            catch (ArgumentException) { continue; }
            yield return candidate;
        }
    }
}