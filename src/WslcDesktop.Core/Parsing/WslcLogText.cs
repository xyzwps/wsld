namespace WslcDesktop.Core.Parsing;

/// <summary>
/// 文本行切分与合并工具,供 CLI 输出解析与拉取进度等模块共用。
/// </summary>
public static class WslcLogText
{
    public const string DisplayNewLine = "\r\n";

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

    public static string Join(IReadOnlyList<string> lines) => string.Join(DisplayNewLine, lines);

    public static string Join(IEnumerable<string> lines) => string.Join(DisplayNewLine, lines);
}
