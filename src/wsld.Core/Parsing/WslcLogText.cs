using System.Globalization;
using System.Text.RegularExpressions;
using static wsld.Core.I18n.Loc;

namespace wsld.Core.Parsing;

/// <summary>
/// 把 `wslc logs` 的原始文本整理成界面能直接显示的行,同时供 CLI 输出解析与拉取进度共用行切分。
/// 实测:行尾只有 LF;`-t` 给的是 UTC 纳秒串(如 2026-10-12T11:34:46.874570304Z),又长又难读。
/// </summary>
public static class WslcLogText
{
    /// <summary>只读文本框需要 CRLF 才会换行,单个 LF 不保险。</summary>
    public const string DisplayNewLine = "\r\n";

    /// <summary>单行最多显示这么多字符,再多就截断(防止一条异常长的日志把界面钉死)。</summary>
    public const int MaxLineChars = 4_000;

    /// <summary>截断标记。</summary>
    public static string TruncatedSuffix => T("log.truncated");

    private static readonly Regex TimestampPrefix = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})) ",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 原始 stdout → 行数组。空输入给空数组;末尾那个由换行符产生的空行会被丢掉,
    /// 中间的空行原样保留 —— 那确实是日志的一部分。
    /// </summary>
    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return Array.Empty<string>();

        var pieces = raw.Replace("\r\n", "\n").Split('\n');
        var lines = new List<string>(pieces.Length);
        foreach (var piece in pieces)
        {
            lines.Add(piece.EndsWith('\r') ? piece[..^1] : piece);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>整段文本 → 已经排好版的行(时间戳美化 + 超长截断)。</summary>
    public static IReadOnlyList<string> Prepare(string? raw, bool timestamps) =>
        Prepare(Split(raw), timestamps);

    /// <summary>逐行 → 已经排好版的行。</summary>
    public static IReadOnlyList<string> Prepare(IEnumerable<string> rawLines, bool timestamps) =>
        rawLines.Select(line => Pretty(line, timestamps)).ToList();

    /// <summary>
    /// 单行整理。只有开启时间戳时才动行首,解析不了就原样返回,
    /// 绝不猜测;未开启时哪怕行首长得像时间戳也不改。
    /// </summary>
    public static string Pretty(string line, bool timestamps)
    {
        var text = timestamps ? ReplaceTimestampPrefix(line) : line;
        return text.Length > MaxLineChars
            ? text[..MaxLineChars] + TruncatedSuffix
            : text;
    }

    /// <summary>把 UTC 时间戳前缀换成本地时间的 时:分:秒.毫秒。</summary>
    private static string ReplaceTimestampPrefix(string line)
    {
        var match = TimestampPrefix.Match(line);
        if (!match.Success) return line;

        var raw = match.Groups["ts"].Value;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return line;
        }

        // 9 位纳秒超出 .NET 的 100ns 精度,解析会自动截到 7 位,时间点不变。
        var local = parsed.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return local + line[raw.Length..];
    }

    /// <summary>行数组 → 文本框要的一次性写入的整块文本。</summary>
    public static string Join(IReadOnlyList<string> lines) => string.Join(DisplayNewLine, lines);

    /// <summary>行数组 → 整块文本。</summary>
    public static string Join(IEnumerable<string> lines) => string.Join(DisplayNewLine, lines);
}
