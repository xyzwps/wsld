namespace WslcDesktop.Core;

/// <summary>
/// 将后台线程上的状态更新调度回 UI 线程。
/// 为 null 时 ViewModel 直接同步执行(单元测试场景)。
/// </summary>
public interface IUIThread
{
    void Post(Action action);
}