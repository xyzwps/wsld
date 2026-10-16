namespace wsld.Core.Parsing;

/// <summary>一条容器标签。<see cref="HasKey"/> 为空时界面只显示值那一行。</summary>
public sealed record ContainerLabel(string Key, string Value)
{
    public bool HasKey => Key.Length > 0;
}

/// <summary>
/// 把 wslc 输出的 <c>Labels</c> 串拆成键值对(功能 001 · AC4 的 Labels 字段)。
///
/// wslc 只是把各标签用逗号拼起来,而标签值自己就可能带逗号
/// (镜像描述、<c>com.microsoft.wsl.container.metadata</c> 那串 JSON 都是),
/// 所以这里按"逗号 + 后面紧跟一个像标签名的键 + <c>=</c>"才认边界,
/// 并且跳过引号与花/方括号内部。这终究是启发式:wslc 交出来的串本身就有歧义,
/// 一个值里写了 <c>,foo=bar</c> 就会被拆错 —— 那种情况宁可多拆一条,也不能丢数据。
/// </summary>
public static class ContainerLabelParser
{
    public static IReadOnlyList<ContainerLabel> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var cuts = FindBoundaries(raw);
        var result = new List<ContainerLabel>(cuts.Count + 1);
        var from = 0;
        foreach (var cut in cuts)
        {
            Add(result, raw[from..cut]);
            from = cut + 1;
        }
        Add(result, raw[from..]);
        return result;
    }

    /// <summary>逗号处切一刀的条件:它在引号与括号之外,且后面是 <c>键名=</c>。</summary>
    private static List<int> FindBoundaries(string s)
    {
        var cuts = new List<int>();
        var depth = 0;
        var inQuotes = false;
        var escaped = false;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];

            if (inQuotes)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inQuotes = false;
                continue;
            }

            switch (c)
            {
                case '"': inQuotes = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']': if (depth > 0) depth--; break;
                case ',' when depth == 0 && LooksLikeNextLabel(s, i + 1): cuts.Add(i); break;
            }
        }
        return cuts;
    }

    private static bool LooksLikeNextLabel(string s, int at)
    {
        var i = at;
        while (i < s.Length && s[i] == ' ') i++;

        var start = i;
        while (i < s.Length && IsKeyChar(s[i])) i++;
        return i > start && i < s.Length && s[i] == '=';
    }

    /// <summary>标签名字符集:字母数字加 <c>. - _ / :</c>(前两者是 docker 惯例,冒号与斜杠来自镜像仓库前缀)。</summary>
    private static bool IsKeyChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or ':';

    private static void Add(List<ContainerLabel> to, string segment)
    {
        var text = segment.Trim();
        if (text.Length == 0) return;

        var eq = text.IndexOf('=');
        to.Add(eq < 0 ? new ContainerLabel("", text) : new ContainerLabel(text[..eq].Trim(), text[(eq + 1)..].Trim()));
    }
}
