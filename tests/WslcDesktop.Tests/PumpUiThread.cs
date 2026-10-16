using System.Collections.Concurrent;

namespace WslcDesktop.Tests;

using WslcDesktop.Core;

/// <summary>
/// 用一条专职工作线程持续泵 IUIThread.Post 送来的动作,充当"UI 线程"。
/// 存在的意义:钉住"状态清零与 PropertyChanged 必须发生在 UI 线程"这类回归 ——
/// ConfigureAwait(false) 之后在线程池线程上发的通知,x:Bind 收不到,界面会永远转圈。
/// </summary>
public sealed class PumpUiThread : IUIThread, IDisposable
{
    private readonly BlockingCollection<(Action Act, TaskCompletionSource Done)> _queue = new();
    private readonly Thread _thread;

    public PumpUiThread()
    {
        _thread = new Thread(() =>
        {
            UiThreadId = Environment.CurrentManagedThreadId;
            foreach (var (act, done) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    act();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }
        })
        {
            IsBackground = true,
        };
        _thread.Start();
        SpinWait.SpinUntil(() => UiThreadId != 0, TimeSpan.FromSeconds(5));
    }

    /// <summary>泵线程的托管线程 id;0 表示还没起来。</summary>
    public int UiThreadId { get; private set; }

    public void Post(Action action) =>
        _queue.Add((action, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
