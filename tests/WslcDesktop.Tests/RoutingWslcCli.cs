using WslcDesktop.Core.Parsing;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 按命令动词路由的假 CLI。
/// 功能 004 用它区分"列表"和"操作";功能 005 加上容器列表(日志页的下拉)与逐行回调(跟随)。
/// 之所以要路由,是因为一次操作成功后 ViewModel 会再刷新一次列表,
/// 单纯按顺序排脚本的命令序列很容易被刷新调用打乱。
/// </summary>
public sealed class RoutingWslcCli : IWslcCli
{
    private readonly Queue<WslcCliResult> _scriptedResults = new();
    private readonly Queue<(int ExitCode, IReadOnlyList<string> Lines, string StdErr)> _scriptedStreams = new();

    /// <summary>镜像列表命令的返回内容(NDJSON)。</summary>
    public string ListJson { get; set; } = "";

    /// <summary>容器列表命令(list -a --format json)的返回内容(NDJSON),功能 005 的容器下拉用它(AC15)。</summary>
    public string ContainersJson { get; set; } = "";

    /// <summary>非列表命令的接管者;用于制造"进程还在跑"的场景。</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<WslcCliResult>>? ActionHandler { get; set; }

    /// <summary>逐行回调命令的接管者;用于制造"跟随还在流"的场景(AC7/AC8/AC12)。</summary>
    public Func<IReadOnlyList<string>, Action<string>?, CancellationToken, Task<WslcCliResult>>? StreamHandler { get; set; }

    public List<IReadOnlyList<string>> Calls { get; } = new();

    /// <summary>只记录走了逐行回调路径的调用。</summary>
    public List<IReadOnlyList<string>> StreamCalls { get; } = new();

    public RoutingWslcCli Enqueue(int exitCode, string stdout = "", string stderr = "")
    {
        _scriptedResults.Enqueue(new WslcCliResult(exitCode, stdout, stderr));
        return this;
    }

    /// <summary>排一个逐行返回的脚本(AC7):每行立刻交给回调。</summary>
    public RoutingWslcCli EnqueueStream(int exitCode, string stdout, string stderr = "")
    {
        _scriptedStreams.Enqueue((exitCode, WslcLogText.Split(stdout), stderr));
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

    public int CountStreamCalls(string verb) => StreamCalls.Count(args => args.Count > 0 && args[0] == verb);

    public IReadOnlyList<string> LastCall(string verb) =>
        Calls.LastOrDefault(args => args.Count > 0 && args[0] == verb) ?? Array.Empty<string>();

    public IReadOnlyList<string> LastStreamCall(string verb) =>
        StreamCalls.LastOrDefault(args => args.Count > 0 && args[0] == verb) ?? Array.Empty<string>();

    public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments);

        if (arguments.Count > 0 && arguments[0] == "images")
        {
            return Task.FromResult(new WslcCliResult(0, ListJson, ""));
        }

        if (arguments.Count > 0 && arguments[0] == "list")
        {
            return Task.FromResult(new WslcCliResult(0, ContainersJson, ""));
        }

        if (ActionHandler is not null)
        {
            return ActionHandler(arguments, cancellationToken);
        }

        return FromScriptAsync();
    }

    public Task<WslcCliResult> RunLinesAsync(
        IReadOnlyList<string> arguments,
        Action<string>? onStdoutLine,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(arguments);
        StreamCalls.Add(arguments);

        if (StreamHandler is not null)
        {
            return StreamHandler(arguments, onStdoutLine, cancellationToken);
        }

        if (_scriptedStreams.Count > 0)
        {
            var (exitCode, lines, stderr) = _scriptedStreams.Dequeue();
            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                onStdoutLine?.Invoke(line);
            }

            return Task.FromResult(new WslcCliResult(exitCode, string.Join("\n", lines), stderr));
        }

        return FromScriptAsync();
    }

    private Task<WslcCliResult> FromScriptAsync() =>
        Task.FromResult(_scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : new WslcCliResult(0, "", ""));
}