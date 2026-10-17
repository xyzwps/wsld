using wsld.Core.Parsing;

namespace wsld.Core.Models;

/// <summary>
/// 一次 <c>wslc logs</c> 的结果(功能 005)。
/// 里面的行都已经过 <see cref="WslcLogText"/> 整理(拆行、时间戳、截断),界面拿到就能直接显示。
/// 实测:退出码 0 + 空输出是合法结果(容器根本没打印东西),所以这里只有行数,不区分成功失败 ——
/// 失败一律以 <see cref="WslcCli.WslcCliException"/> 抛出。
/// </summary>
public sealed record LogSnapshot(IReadOnlyList<string> Lines)
{
    /// <summary>没有任何输出的结果。</summary>
    public static LogSnapshot Empty { get; } = new(Array.Empty<string>());

    public int LineCount => Lines.Count;

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>可直接塞进只读文本框的整块文本。</summary>
    public string Text => WslcLogText.Join(Lines);
}
