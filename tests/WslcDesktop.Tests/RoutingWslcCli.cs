using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 004 用的假 CLI:`images --format json` 固定返回给定内容,
/// 其余命令(pull / rmi)按脚本返回,或者交给 <see cref="ActionHandler"/> 由测试决定何时结束。
/// 之所以要区分,是因为一次拉取成功后 ViewModel 会再刷新一次列表,
/// 单纯按顺序排脚本的命令序列很容易被刷新调用打乱。
/// </summary>
public sealed class RoutingWslcCli : IWslcCli
{
    private readonly Queue<WslcCliResult> _scriptedResults = new();

    /// <summary>列表命令的返回内容(NDJSON)。</summary>
    public string ListJson { get; set; } = "";

    /// <summary>非列表命令的接管者;用于制造"进程还在跑"的场景。</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<WslcCliResult>>? ActionHandler { get; set; }

    public List<IReadOnlyList<string>> Calls { get; } = new();

    public RoutingWslcCli Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _scriptedResults.Enqueue(new WslcCliResult(exitCode, stdout, stderr));
        return this;
    }

    /// <summary>某个动词(如 rmi)永远返回给定结果,其余命令仍走脚本队列。</summary>
    public RoutingWslcCli AlwaysFor(string verb, int exitCode, string stdout = "", string stderr = "")
    {
        ActionHandler = (args, _) => args.Count > 0 && args[0] == verb
            ? Task.FromResult(new WslcCliResult(exitCode, stdout, stderr))
            : FromScriptAsync();
        return this;
    }

    public int CountCalls(string verb) => Calls.Count(args => args.Count > 0 && args[0] == verb);

    public IReadOnlyList<string> LastCall(string verb) =>
        Calls.LastOrDefault(args => args.Count > 0 && args[0] == verb) ?? Array.Empty<string>();

    public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments);

        if (arguments.Count > 0 && arguments[0] == "images")
        {
            return Task.FromResult(new WslcCliResult(0, ListJson, ""));
        }

        if (ActionHandler is not null)
        {
            return ActionHandler(arguments, cancellationToken);
        }

        return FromScriptAsync();
    }

    private Task<WslcCliResult> FromScriptAsync() =>
        Task.FromResult(_scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : new WslcCliResult(0, "", ""));
}