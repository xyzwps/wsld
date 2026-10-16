using System.Text;

namespace wsld.Core.Parsing;

/// <summary>
/// 把子进程在终端里写出的 ANSI/VT 字节流还原成"屏幕上此刻可见的行"(功能 011)。
/// 只做够用的一小撮语义:wslc 的进度渲染是"绝对定位 + 写一行 + CR LF 前进一步",
/// 因此我们维护一个光标,把字符落到对应行,支持 CUP 定位、ED/ECH 擦除、OSL 标题序列跳过。
/// 不追求实现完整 VT100;遇到不认识的转义序列就整段丢弃。
/// 仅供单线程使用(读流线程),<see cref="Snapshot"/> 返回独立副本,可以放心传到 UI 线程。
/// </summary>
public sealed class VtScreenTracker
{
    private readonly List<StringBuilder> _rows = new();
    private int _cursorRow;
    private int _cursorColumn;
    private int _state; // 0=正常 1=转义中(等[或]) 2=CSI 参数收集 3=OSC 内容

    private readonly List<char> _csiParameters = new();
    private int _csiPrivate; // '?' 前缀

    /// <summary>终端高度;写超了就向上滚动(行数封顶,防止内存无上限增长)。</summary>
    public int Height { get; }

    public VtScreenTracker(int height = 200)
    {
        Height = Math.Max(24, height);
    }

    /// <summary>喂一段解码后的文本;返回屏幕内容是否发生了变化。</summary>
    public bool Feed(string text)
    {
        var changed = false;
        foreach (var ch in text)
        {
            switch (_state)
            {
                case 0:
                    changed |= OnTextChar(ch);
                    break;
                case 1:
                    if (ch == '[') _state = 2;
                    else if (ch == ']') { _state = 3; _csiPrivate = 0; }
                    else if (ch == '\\') _state = 0; // ST 终止(前面没有 CSI)
                    else _state = 0; // 两字符转义(如 ESC 7),忽略
                    break;
                case 2: // CSI 参数
                    if (ch >= '0' && ch <= '9' || ch == ';' || ch == ':') _csiParameters.Add(ch);
                    else if (ch == '?' || ch == '>') _csiPrivate = 1;
                    else if (ch == '\x1b') { _state = 1; } // 罕见:转义中又转义
                    else { changed |= OnCsiFinal(ch); _state = 0; }
                    break;
                case 3: // OSC 内容,直到 BEL 或 ESC \
                    if (ch == '\a') _state = 0;
                    else if (ch == '\x1b') _state = 5;
                    break;
                case 5: // OSC 里等 '\' 收尾
                    _state = ch == '\\' ? 0 : 1;
                    break;
            }
        }

        return changed;
    }

    /// <summary>当前屏幕上非空的行(自上而下,去尾部空白)。</summary>
    public IReadOnlyList<string> Snapshot()
    {
        var lines = new List<string>(_rows.Count);
        foreach (var row in _rows)
        {
            var text = row.ToString().TrimEnd();
            if (text.Length > 0) lines.Add(text);
        }

        return lines;
    }

    private bool OnTextChar(char ch)
    {
        switch (ch)
        {
            case '\x1b':
                _state = 1;
                return false;
            case '\r':
                _cursorColumn = 0;
                return false;
            case '\n' or '\x0b' or '\x0c':
                MoveToRow(_cursorRow + 1);
                return false;
            case '\b':
                _cursorColumn = Math.Max(0, _cursorColumn - 1);
                return false;
            case '\t':
                // 制表位只移动光标:经过的格子在真终端里就是空白,把行补到那个位置够了。
                // 别顺手改成调 Put:Put 的 afterColumn 是"写完之后的列",而 TAB 要的恰好是它前一个格子的下标。
                MoveToTabStop();
                return false;
            default:
                if (ch < ' ') return false; // 其它控制字符忽略
                return Put(ch, _cursorColumn + 1);
        }
    }

    private bool Put(char ch, int afterColumn)
    {
        EnsureRow(_cursorRow);
        var row = _rows[_cursorRow];
        var needed = Math.Max(afterColumn, row.Length);
        if (row.Length < needed) row.Append(' ', needed - row.Length);
        row[_cursorColumn] = ch;
        _cursorColumn = afterColumn;
        return true;
    }

    private bool OnCsiFinal(char final)
    {
        var parameters = _csiParameters.ToArray();
        _csiParameters.Clear();
        var privateSequence = _csiPrivate != 0;
        _csiPrivate = 0;

        if (privateSequence) return false; // DECSET/DECRST(光标显隐、模式开关)与我们无关

        switch (final)
        {
            case 'H' or 'f':
            {
                var parts = Split(parameters);
                var row = parts.Count > 0 && parts[0] > 0 ? parts[0] : 1;
                var col = parts.Count > 1 && parts[1] > 0 ? parts[1] : 1;
                _cursorRow = Math.Min(row - 1, Height - 1);
                _cursorColumn = col - 1;
                return false;
            }
            case 'J': // 清屏
            {
                var parts = Split(parameters);
                var mode = parts.Count > 0 ? parts[0] : 0;
                if (mode is 2 or 3)
                {
                    // 屏幕上本来就没东西时不许报告"变了":wslc 每次渲染都先 [2J,
                    // 照实返回 true 会让界面每秒白刷一次(调用方靠这个返回值决定要不要回调)。
                    var had = _rows.Count > 0;
                    _rows.Clear();
                    _cursorRow = 0;
                    _cursorColumn = 0;
                    return had;
                }

                return false;
            }
            case 'K': // 行内擦除
            {
                var parts = Split(parameters);
                var mode = parts.Count > 0 ? parts[0] : 0;
                EnsureRow(_cursorRow);
                var row = _rows[_cursorRow];
                return mode is 0 or 2 && TruncateTo(row, mode == 2 ? 0 : _cursorColumn);
            }
            case 'X': // 擦除指定个字符:简化为"截断到光标处",后续字符会覆盖回来
            {
                EnsureRow(_cursorRow);
                return TruncateTo(_rows[_cursorRow], _cursorColumn);
            }
            case 'm': // 颜色/样式,忽略
            case 'A' or 'B' or 'C' or 'D': // 相对移动(实测 wslc 不用,但便宜就顺手支持)
            {
                if (final is not 'm')
                {
                    var parts = Split(parameters);
                    var n = parts.Count > 0 && parts[0] > 0 ? parts[0] : 1;
                    switch (final)
                    {
                        case 'A': MoveToRow(_cursorRow - n); break;
                        case 'B': MoveToRow(_cursorRow + n); break;
                        case 'C': _cursorColumn += n; break;
                        case 'D': _cursorColumn = Math.Max(0, _cursorColumn - n); break;
                    }
                }

                return final != 'm';
            }
            default:
                return false; // 未知 CSI:丢弃
        }
    }

    private static List<int> Split(char[] parameters)
    {
        var text = new string(parameters);
        if (text.Length == 0) return new List<int> { 0 };
        return text.Split(';', ':').Select(part => int.TryParse(part, out var value) ? value : 0).ToList();
    }

    private void MoveToRow(int row)
    {
        if (row >= Height)
        {
            // 极端场景(层数多于终端高度)才滚动:丢掉最早的行,整体向上挪。
            var excess = row - Height + 1;
            var drop = Math.Min(excess, _rows.Count);
            _rows.RemoveRange(0, drop);
            row -= excess;
        }

        _cursorRow = Math.Max(0, row);
        // VT 的 LF 只下移、不动列;wslc 每行都以 CR 开头,所以列保持不变即可。
    }

    /// <summary>光标前进到下一个 8 列制表位(行尾按空白补齐)。</summary>
    private void MoveToTabStop()
    {
        var stop = (_cursorColumn / 8 + 1) * 8;
        EnsureRow(_cursorRow);
        var row = _rows[_cursorRow];
        if (row.Length < stop) row.Append(' ', stop - row.Length);
        _cursorColumn = stop;
    }
    private void EnsureRow(int index)
    {
        while (_rows.Count <= index) _rows.Add(new StringBuilder());
    }

    private static bool TruncateTo(StringBuilder row, int length)
    {
        if (row.Length <= length) return false;
        row.Length = length;
        return true;
    }
}

