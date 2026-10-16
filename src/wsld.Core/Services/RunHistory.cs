using System.Collections.ObjectModel;

namespace wsld.Core.Services;

/// <summary>
/// "就地执行过的那条命令"的历史(功能 012 补强)。
/// 只存在内存里:页面缓存着,切语言重建页面时由 AppServices 的同一个实例续上,
/// 但应用退出即清空 —— 它救的是"这条没跑对,我要接着改"的工作现场,不是档案。
/// ObservableCollection 必须在 UI 线程上改;记录点(执行开始时)天然满足。
/// </summary>
public sealed class RunHistory
{
    /// <summary>保留的最大条数;超出后最旧的被挤掉。</summary>
    public const int Capacity = 100;

    /// <summary>最新在前。</summary>
    public ObservableCollection<RunHistoryEntry> Entries { get; } = new();

    /// <summary>记一条执行过的命令;与最新一条相同则跳过(连点两次执行不该长出两条一样的)。</summary>
    public void Record(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (Entries.Count > 0 && string.Equals(Entries[0].Command, command, StringComparison.Ordinal)) return;

        Entries.Insert(0, new RunHistoryEntry(command, DateTime.Now));
        while (Entries.Count > Capacity) Entries.RemoveAt(Entries.Count - 1);
    }

    public void Clear() => Entries.Clear();
}

/// <summary>一条历史:命令原文 + 执行时刻的展示文本。</summary>
public sealed class RunHistoryEntry(string command, DateTime at)
{
    public string Command { get; } = command;

    /// <summary>列表里显示的本地时间,秒级足够分辨先后。</summary>
    public string TimeText { get; } = at.ToString("MM-dd HH:mm:ss");
}
