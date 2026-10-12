using System.Diagnostics;
using WslcDesktop.Core.Services;
using WslcDesktop.Core.Settings;
using WslcDesktop.Core.ViewModels;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>
/// 功能 010 · AC2/AC3/AC4/AC5/AC6:镜像页在启用镜像源后的拉取编排
/// (改写引用 → wslc pull → wslc tag 改回用户写的名字 → wslc rmi 去掉镜像站标签)。
/// 假 CLI 按动词路由,所以"成功后刷新一次"不会打乱 pull/tag/rmi 的脚本顺序。
/// </summary>
public class ImagesPageMirrorPullTests
{
    private const string BusyBox = """{"ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox","Tag":"latest","Size":"4.45MB","Containers":"0","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"<none>"}""";
    private const string Hello = """{"ID":"e2ac70e7319a","Repository":"docker.1ms.run/library/hello-world","Tag":"latest","Size":"10.1kB","Containers":"0","CreatedAt":"2026-03-24 05:33:59 +0800 GMT+8","Digest":"<none>"}""";
    private const string TwoImages = BusyBox + "\n" + Hello;

    private const string Typed = "redis:7";
    private const string ViaMirror = "docker.1ms.run/library/redis:7";

    private const string PullNewStatus = "Status: Downloaded newer image for " + ViaMirror;

    private const string TagFailStdErr = """
        来自引擎的错误信息: No such image: docker.1ms.run/library/redis:7
        错误代码： WSLC_E_IMAGE_NOT_FOUND
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    private const string RmiFailStdErr = """
        来自引擎的错误信息: conflict: unable to remove repository reference "docker.1ms.run/library/redis:7"
        错误代码： ERROR_SHARING_VIOLATION
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    // ---------- AC2 / AC5 · 改写 + 改名 ----------

    [Fact]
    public async Task Pull_WithMirror_RewritesReferenceThenRenamesBack()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, FixtureLoader.Read("wslc-pull-new.txt")) // pull
            .Enqueue(0)                                         // tag
            .Enqueue(0);                                        // rmi(只是去掉镜像站那个标签)
        var vm = Create(cli, MirrorSettings());
        vm.PullText = Typed;

        var result = await vm.PullAsync();

        Assert.NotNull(result);
        Assert.Equal(new[] { "pull", ViaMirror }, cli.LastCall("pull"));   // AC2:实际拉的是改写后的引用
        Assert.Equal(new[] { "tag", ViaMirror, Typed }, cli.LastCall("tag"));
        Assert.Equal(new[] { "rmi", ViaMirror }, cli.LastCall("rmi"));
        Assert.Equal(new[] { "pull", "tag", "rmi" }, ActionVerbs(cli));     // AC5:顺序不能反,反了就把镜像删了
        Assert.Equal(Typed, result!.Reference);                             // 提示按用户写的名字报
        Assert.Equal("已拉取 redis:7", vm.Notice);
        Assert.False(vm.HasError);                                          // AC6:改名成功就不该有红条
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Pull_WithMirror_RefreshesTheListExactlyOnceAfterRenaming()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, PullNewStatus).Enqueue(0).Enqueue(0);
        var vm = Create(cli, MirrorSettings());
        vm.PullText = Typed;
        var before = cli.CountCalls("images");

        var result = await vm.PullAsync();

        Assert.NotNull(result);
        Assert.Equal(before + 1, cli.CountCalls("images"));   // 改名做完之后才刷新,列表里直接是新名字
    }

    [Fact]
    public async Task Pull_WithMirror_RenameCanBeTurnedOffInSettings()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }.Enqueue(0, PullNewStatus);
        var vm = Create(cli, new AppSettings { RegistryMirror = "docker.1ms.run", RenameMirrorPull = false });
        vm.PullText = Typed;

        var result = await vm.PullAsync();

        Assert.Equal(new[] { "pull", ViaMirror }, cli.LastCall("pull"));
        Assert.Equal(0, cli.CountCalls("tag"));
        Assert.Equal(0, cli.CountCalls("rmi"));
        Assert.Equal("已拉取 " + ViaMirror, vm.Notice);   // 没改名,就按镜像站那个全名报
    }

    // ---------- AC3 · 不该改写的引用 ----------

    [Theory]
    [InlineData("ghcr.io/org/app:1")]
    [InlineData("mcr.microsoft.com/dotnet/runtime:8.0")]
    [InlineData(ViaMirror)]   // 已经写了镜像站全名,不用再加一层
    public async Task Pull_NonHubReference_IsPulledAsTypedAndNeverRenamed(string reference)
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }.Enqueue(0, "Status: Downloaded newer image for " + reference);
        var vm = Create(cli, MirrorSettings());
        vm.PullText = reference;

        await vm.PullAsync();

        Assert.Equal(new[] { "pull", reference }, cli.LastCall("pull"));
        Assert.Equal(0, cli.CountCalls("tag"));
        Assert.Equal(0, cli.CountCalls("rmi"));
        Assert.Equal("已拉取 " + reference, vm.Notice);
    }

    [Fact]
    public async Task Pull_WithoutMirror_PullsExactlyWhatWasTyped()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }.Enqueue(0, "Status: Downloaded newer image for redis:7");
        var vm = Create(cli, new AppSettings());
        vm.PullText = Typed;

        await vm.PullAsync();

        Assert.Equal(new[] { "pull", Typed }, cli.LastCall("pull"));
        Assert.Equal(0, cli.CountCalls("tag"));
    }

    // ---------- AC6 · 改名失败不能把成功说成失败 ----------

    [Fact]
    public async Task Pull_RenameAfterMirrorFails_ReportsPulledImagePlusAWarning()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, PullNewStatus)
            .Enqueue(1, "", TagFailStdErr);
        var vm = Create(cli, MirrorSettings());
        vm.PullText = Typed;

        var result = await vm.PullAsync();

        Assert.NotNull(result);                                  // 镜像已经在本机,这次拉取是成功的
        Assert.Equal(ViaMirror, result!.Reference);              // 没改成名字,提示就按镜像站的写
        Assert.Equal("已拉取 " + ViaMirror, vm.Notice);           // 绿色提示
        Assert.True(vm.HasError);                                // 同时一条红色补充说明
        Assert.StartsWith("镜像已经拉到本机(", vm.Error);
        Assert.Contains("WSLC_E_IMAGE_NOT_FOUND", vm.Error);
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error);   // 样板提示被清洗
        Assert.Equal(0, cli.CountCalls("rmi"));                  // 新名字没打上,绝不能去掉旧标签
    }

    [Fact]
    public async Task Pull_OldMirrorTagRemovalFails_ReportsRenamedNamePlusAWarning()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages }
            .Enqueue(0, PullNewStatus)
            .Enqueue(0)
            .Enqueue(1, "", RmiFailStdErr);
        var vm = Create(cli, MirrorSettings());
        vm.PullText = Typed;

        var result = await vm.PullAsync();

        Assert.NotNull(result);
        Assert.Equal("已拉取 redis:7", vm.Notice);                // 名字已经改过来,按用户写的报
        Assert.StartsWith("已经改名为 redis:7,但带镜像站前缀的那个标签没能去掉:", vm.Error);
        Assert.Contains("ERROR_SHARING_VIOLATION", vm.Error);
        Assert.DoesNotContain("如果此错误是意外错误", vm.Error);
    }

    // ---------- AC5 · 取消之后不做改名 ----------

    [Fact]
    public async Task Pull_Cancelled_BeforeItFinishes_DoesNotRename()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        cli.ActionHandler = (_, ct) => Task.FromException<WslcCliResult>(new OperationCanceledException(ct));
        var vm = Create(cli, MirrorSettings());
        vm.PullText = Typed;

        var result = await vm.PullAsync();

        Assert.Null(result);
        Assert.Equal(0, cli.CountCalls("tag"));
        Assert.Equal(0, cli.CountCalls("rmi"));
        Assert.Equal("已取消拉取 " + ViaMirror, vm.Notice);   // 说的是真正被取消的那次拉取
    }

    [Fact]
    public async Task Pull_WhileItIsRunning_BusyTextNamesTheMirrorReference()
    {
        var cli = new RoutingWslcCli { ListJson = TwoImages };
        var gate = new PullGate();
        cli.ActionHandler = gate.Handler;
        var vm = Create(cli, MirrorSettings());
        vm.PullText = Typed;

        var pull = vm.PullAsync();
        await WaitUntilAsync(() => vm.IsBusy);
        vm.Tick();

        Assert.Matches(@"^正在拉取 docker\.1ms\.run/library/redis:7 · 已等待 \d+ 秒$", vm.BusyText);

        gate.Complete(PullNewStatus);
        await pull;

        Assert.Equal(new[] { "tag", ViaMirror, Typed }, cli.LastCall("tag"));
        Assert.False(vm.IsBusy);
    }

    // ---------- AC4 · 输入框下面那行"实际会拉…" ----------

    [Fact]
    public void PullMirrorHint_ShowsTheRewrittenReference()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages },
            new AppSettings { RegistryMirror = "https://docker.1ms.run/" });

        vm.PullText = Typed;
        Assert.True(vm.HasPullMirrorHint);
        Assert.Equal("镜像源实际会拉取:" + ViaMirror, vm.PullMirrorHint);
        Assert.Equal(ViaMirror, vm.EffectivePullReference);

        vm.PullText = "ghcr.io/actions/runner:2";   // AC3:不该改写的引用,那行说明也不该出现
        Assert.False(vm.HasPullMirrorHint);
        Assert.Equal("ghcr.io/actions/runner:2", vm.EffectivePullReference);
        Assert.Equal("", vm.PullMirrorHint);
    }

    [Fact]
    public void PullMirrorHint_IsQuietWhenNoMirrorIsConfigured()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages }, new AppSettings());
        vm.PullText = Typed;

        Assert.False(vm.HasPullMirrorHint);
        Assert.Equal("", vm.PullMirrorHint);
        Assert.Equal(Typed, vm.EffectivePullReference);
    }

    [Fact]
    public void RefreshMirrorState_RecomputesTheHintAfterTheSettingsPageChangesTheMirror()
    {
        // 设置页与镜像页共用同一个 AppSettings 实例,页面又是缓存的,只能靠这个事件把预览推平。
        var settings = new AppSettings();
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages }, settings);
        vm.PullText = Typed;
        Assert.False(vm.HasPullMirrorHint);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.RegistryMirror = "docker.1ms.run";
        vm.RefreshMirrorState();

        Assert.Contains("PullMirrorHint", raised);
        Assert.Contains("HasPullMirrorHint", raised);
        Assert.Contains("EffectivePullReference", raised);
        Assert.True(vm.HasPullMirrorHint);
    }

    [Fact]
    public void ChangingPullText_AlsoRefreshesTheMirrorHint()
    {
        var vm = Create(new RoutingWslcCli { ListJson = TwoImages }, MirrorSettings());

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PullText = "bitnami/redis";

        Assert.Contains("PullMirrorHint", raised);
        Assert.Equal("镜像源实际会拉取:docker.1ms.run/bitnami/redis", vm.PullMirrorHint);
    }

    // ---------- 辅助 ----------

    private static AppSettings MirrorSettings() => new() { RegistryMirror = "docker.1ms.run" };

    private static ImagesPageViewModel Create(RoutingWslcCli cli, AppSettings settings) =>
        new(new ImagesService(cli), uiThread: null, settings);

    /// <summary>这一次拉取里真正启动的 wslc 动作(列表查询不算),用来核对 pull→tag→rmi 的顺序。</summary>
    private static List<string> ActionVerbs(RoutingWslcCli cli) =>
        cli.Calls.Where(a => a.Count > 0 && a[0] != "images").Select(a => a[0]).ToList();

    /// <summary>把 pull 挂起、tag/rmi 直接成功的假 CLI 接管者。</summary>
    private sealed class PullGate
    {
        private readonly TaskCompletionSource<WslcCliResult> _pull = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WslcCliResult> Handler(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            if (arguments.Count > 0 && arguments[0] == "pull")
            {
                cancellationToken.Register(() => _pull.TrySetCanceled(cancellationToken));
                return _pull.Task;
            }

            return Task.FromResult(new WslcCliResult(0, "", ""));
        }

        public void Complete(string stdout, int exitCode = 0, string stderr = "") =>
            _pull.TrySetResult(new WslcCliResult(exitCode, stdout, stderr));
    }

    /// <summary>等待后台状态落地。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("等待条件超时。");
            await Task.Delay(10);
        }
    }
}