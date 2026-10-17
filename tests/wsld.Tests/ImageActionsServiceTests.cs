using wsld.Core.Services;
using wsld.Core.WslcCli;

namespace wsld.Tests;

/// <summary>册子 IA-001、IA-003、IA-005、IA-006、IA-007、IA-013、IA-014、IA-015:镜像动作的命令拼装、输入校验、结果解析与错误传播。</summary>
public class ImageActionsServiceTests
{
    private const string BusyBoxRef = "docker.1ms.run/library/busybox:latest";

    /// <summary>实测 stderr(删掉被容器引用的镜像),原样保留样板提示。</summary>
    private const string ConflictStdErr = """
        conflict: unable to remove repository reference "docker.1ms.run/library/busybox:latest" (must force) - container f47fdddf9219 is using its referenced image aaef90e06523

        错误代码： ERROR_SHARING_VIOLATION
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    private const string NotFoundStdErr = """
        来自引擎的错误信息: No such image: nope-004:latest
        错误代码： WSLC_E_IMAGE_NOT_FOUND
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    private const string PullFailureStdErr = """
        来自引擎的错误信息: Get "https://registry-1.docker.io/v2/": context deadline exceeded
        错误代码： E_FAIL
        如果此错误是意外错误，请考虑搜索现有问题或在 https://github.com/microsoft/WSL/issues 提交新问题。
        """;

    // ---------- 拉取命令映射 ----------

    [Fact]
    [Trait("case", "IA-001")]
    public async Task PullAsync_InvokesWslcPullWithTrimmedReference()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var service = new ImagesService(cli);

        await service.PullAsync($"  {BusyBoxRef}  ");

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "pull", BusyBoxRef }, args);
    }

    [Fact]
    [Trait("case", "IA-001")]
    public async Task PullAsync_UsesDetailedOutputNotQuietFlag()
    {
        // 实测 -q 会把 Status: 行吃掉,拿不到"新下载/已最新"的区分,所以必须用详细输出。
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-new.txt"));
        var service = new ImagesService(cli);

        await service.PullAsync("docker.1ms.run/library/nginx:latest");

        Assert.DoesNotContain("-q", cli.Calls.Single());
    }

    // ---------- 拉取结果解析 ----------

    [Fact]
    [Trait("case", "IA-005")]
    public async Task PullAsync_NewlyDownloadedOutput_ReportsNotUpToDate()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-new.txt"));
        var service = new ImagesService(cli);

        var result = await service.PullAsync("docker.1ms.run/library/nginx:latest");

        Assert.False(result.IsUpToDate);
        Assert.Contains("Downloaded newer image", result.StatusLine);
        Assert.Equal("已拉取 docker.1ms.run/library/nginx:latest", result.Notice);
    }

    [Fact]
    [Trait("case", "IA-006")]
    public async Task PullAsync_UpToDateOutput_ReportsUpToDate()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var service = new ImagesService(cli);

        var result = await service.PullAsync(BusyBoxRef);

        Assert.True(result.IsUpToDate);
        Assert.Equal("docker.1ms.run/library/busybox:latest 已是最新", result.Notice);
    }

    [Fact]
    [Trait("case", "IA-007")]
    public async Task PullAsync_OutputWithoutStatusLine_DoesNotThrow()
    {
        // 未来输出形态变化(或改用 -q)时不能崩,按"不是最新"处理即可。
        var cli = new FakeWslcCli().Enqueue(0, BusyBoxRef);
        var service = new ImagesService(cli);

        var result = await service.PullAsync(BusyBoxRef);

        Assert.False(result.IsUpToDate);
        Assert.Equal("", result.StatusLine);
    }

    [Fact]
    [Trait("case", "IA-007")]
    public async Task PullAsync_EmptyOutput_DoesNotThrow()
    {
        var cli = new FakeWslcCli().Enqueue(0, "");
        var service = new ImagesService(cli);

        var result = await service.PullAsync(BusyBoxRef);

        Assert.Equal(BusyBoxRef, result.Reference);
        Assert.False(result.IsUpToDate);
    }

    // ---------- 空引用绝不启动进程 ----------

    [Theory]
    [Trait("case", "IA-003")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PullAsync_EmptyReference_ThrowsWithoutStartingProcess(string? reference)
    {
        var cli = new FakeWslcCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PullAsync(reference!));
        Assert.Empty(cli.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RemoveAsync_EmptyReference_ThrowsWithoutStartingProcess(string? reference)
    {
        var cli = new FakeWslcCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveAsync(reference!));
        Assert.Empty(cli.Calls);
    }

    // ---------- 删除命令映射 ----------

    [Fact]
    [Trait("case", "IA-001")]
    public async Task RemoveAsync_DefaultsToPlainRmi()
    {
        var cli = new FakeWslcCli().Enqueue(0, "已取消标记: wslcd-test:alpha");
        var service = new ImagesService(cli);

        await service.RemoveAsync("wslcd-test:alpha");

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "rmi", "wslcd-test:alpha" }, args);
        Assert.DoesNotContain(ImagesService.ForceFlag, args);
    }

    [Fact]
    [Trait("case", "IA-001")]
    public async Task RemoveAsync_Force_AddsFlagBeforeReference()
    {
        var cli = new FakeWslcCli().Enqueue(0, "已删除: sha256:aaef90e06523");
        var service = new ImagesService(cli);

        await service.RemoveAsync(BusyBoxRef, force: true);

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "rmi", "-f", BusyBoxRef }, args);
    }

    [Fact]
    [Trait("case", "IA-001")]
    public async Task RemoveAsync_TrimsReference()
    {
        var cli = new FakeWslcCli().Enqueue(0);
        var service = new ImagesService(cli);

        await service.RemoveAsync($"  {BusyBoxRef}\t");

        Assert.Equal(new[] { "rmi", BusyBoxRef }, Assert.Single(cli.Calls));
    }

    // ---------- 错误传播 ----------

    [Fact]
    [Trait("case", "IA-013")]
    public async Task PullAsync_NonZeroExit_ThrowsWithPullCommandAndErrorText()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", PullFailureStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.PullAsync("nginx:latest"));

        Assert.Equal("wslc pull", ex.Command);
        Assert.Contains("E_FAIL", ex.Message);
    }

    [Fact]
    [Trait("case", "IA-014")]
    public async Task RemoveAsync_ConflictExit_ThrowsWithRmiCommandAndIsRecognisedAsConflict()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", ConflictStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.RemoveAsync(BusyBoxRef));

        Assert.Equal("wslc rmi", ex.Command);
        Assert.True(ImageRemovalConflict.IsConflict(ex));
    }

    [Fact]
    [Trait("case", "IA-015")]
    public async Task RemoveAsync_NotFoundExit_IsNotAConflict()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", NotFoundStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.RemoveAsync("nope-004:latest"));

        Assert.False(ImageRemovalConflict.IsConflict(ex));
    }

    [Fact]
    public async Task PullAsync_Cancellation_IsPropagated()
    {
        var cli = new CancellingCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PullAsync(BusyBoxRef));
        Assert.Equal(new[] { "pull", BusyBoxRef }, cli.LastArguments);
    }

    [Fact]
    public async Task RemoveAsync_Cancellation_IsPropagated()
    {
        var cli = new CancellingCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RemoveAsync(BusyBoxRef, force: true));
        Assert.Equal(new[] { "rmi", "-f", BusyBoxRef }, cli.LastArguments);
    }

    // ---------- registry-mirror 册 · 打标签(镜像源改名) ----------

    private const string MirrorRef = "docker.1ms.run/library/redis:7";
    private const string PlainRef = "redis:7";

    /// <summary>实测:`wslc tag <源> <目标>` 退出码 0、没有任何输出。</summary>
    [Fact]
    public async Task TagAsync_InvokesWslcTagWithSourceAndTarget()
    {
        var cli = new FakeWslcCli().Enqueue(0, "");
        var service = new ImagesService(cli);

        await service.TagAsync(MirrorRef, PlainRef);

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "tag", MirrorRef, PlainRef }, args);
    }

    [Fact]
    public async Task TagAsync_TrimsBothReferences()
    {
        var cli = new FakeWslcCli().Enqueue(0);
        var service = new ImagesService(cli);

        await service.TagAsync($"  {MirrorRef}\t", $" {PlainRef} ");

        Assert.Equal(new[] { "tag", MirrorRef, PlainRef }, Assert.Single(cli.Calls));
    }

    [Fact]
    public async Task TagAsync_EmptySource_ThrowsWithoutStartingProcess()
    {
        var cli = new FakeWslcCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAsync<ArgumentException>(() => service.TagAsync("   ", PlainRef));
        Assert.Empty(cli.Calls);   // 同一条规矩:空引用绝不启动进程
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TagAsync_EmptyTarget_ThrowsWithoutStartingProcess(string? target)
    {
        var cli = new FakeWslcCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAsync<ArgumentException>(() => service.TagAsync(MirrorRef, target!));
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task TagAsync_NonZeroExit_ThrowsWithTagCommandAndErrorText()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", NotFoundStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.TagAsync(MirrorRef, PlainRef));

        Assert.Equal("wslc tag", ex.Command);
        Assert.Contains("WSLC_E_IMAGE_NOT_FOUND", ex.Message);
    }

    [Fact]
    public async Task TagAsync_Cancellation_IsPropagated()
    {
        var cli = new CancellingCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TagAsync(MirrorRef, PlainRef));
        Assert.Equal(new[] { "tag", MirrorRef, PlainRef }, cli.LastArguments);
    }
    // ---------- pull-progress 册 · 带实时进度的拉取 ----------

    [Fact]
    [Trait("case", "IA-001")]
    public async Task PullWithProgressAsync_InvokesWslcPullThroughTheConsoleWithTrimmedReference()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var service = new ImagesService(cli);

        await service.PullWithProgressAsync($"  {BusyBoxRef}  ");

        // 参数与旧 PullAsync 逐字一致,只是换了输出口(伪终端)
        var args = Assert.Single(cli.ConsoleCalls);
        Assert.Equal(new[] { "pull", BusyBoxRef }, args);
    }

    [Fact]
    public async Task PullWithProgressAsync_HandsTheWholeScreenToTheCallbackOnce()
    {
        var pullOut = FixtureLoader.Read("wslc-pull-new.txt");
        var cli = new FakeWslcCli().Enqueue(0, pullOut);
        var service = new ImagesService(cli);
        var frames = new List<IReadOnlyList<string>>();

        await service.PullWithProgressAsync(BusyBoxRef, frames.Add);

        // 没有中途帧时(普通管道回退,):结束时一次性交出全部屏幕行,内容就是这次拉取的输出
        var frame = Assert.Single(frames);
        Assert.Contains("ecc510c1e359: Pull complete", frame);
        Assert.Contains("Status: Downloaded newer image for docker.1ms.run/library/nginx:latest", frame);
    }

    [Fact]
    public async Task PullWithProgressAsync_StreamsEveryFrameAsItArrives()
    {
        var cli = new FakeWslcCli();
        var service = new ImagesService(cli);
        var frames = new List<IReadOnlyList<string>>();
        cli.ConsoleHandler = (arguments, onVisibleLines, cancellationToken) =>
        {
            // 真机形态:同一块屏幕被反复推上来,每次都是"此刻可见的全部行",不是增量
            onVisibleLines?.Invoke(["latest: Pulling from library/busybox", "ecc510c1e359: Pulling fs layer"]);
            onVisibleLines?.Invoke(["latest: Pulling from library/busybox", "ecc510c1e359: Pull complete"]);
            return Task.FromResult(new WslcCliResult(0, FixtureLoader.Read("wslc-pull-new.txt"), ""));
        };

        await service.PullWithProgressAsync(BusyBoxRef, frames.Add);

        Assert.Equal(2, frames.Count);
        Assert.Equal("ecc510c1e359: Pulling fs layer", frames[0][1]);
        Assert.Equal("ecc510c1e359: Pull complete", frames[1][1]);
    }

    [Fact]
    public async Task PullWithProgressAsync_ReturnsTheSameResultAsPlainPull()
    {
        var pullOut = FixtureLoader.Read("wslc-pull-new.txt");
        var plain = await new ImagesService(new FakeWslcCli().Enqueue(0, pullOut)).PullAsync(BusyBoxRef);
        var progressFrames = 0;
        var streamed = await new ImagesService(new FakeWslcCli().Enqueue(0, pullOut))
            .PullWithProgressAsync(BusyBoxRef, _ => progressFrames++);

        // 换了输出口不能换结论
        Assert.Equal(plain.Reference, streamed.Reference);
        Assert.Equal(plain.IsUpToDate, streamed.IsUpToDate);
        Assert.Equal(plain.Notice, streamed.Notice);
        Assert.Equal(1, progressFrames);
    }

    [Theory]
    [Trait("case", "IA-003")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task PullWithProgressAsync_EmptyReference_ThrowsWithoutStartingProcess(string? reference)
    {
        var cli = new FakeWslcCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PullWithProgressAsync(reference!));

        Assert.Empty(cli.Calls);
    }

    [Fact]
    [Trait("case", "IA-013")]
    public async Task PullWithProgressAsync_NonZeroExit_ThrowsWithPullCommandAndErrorText()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", PullFailureStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.PullWithProgressAsync(BusyBoxRef));

        Assert.Equal("wslc pull", ex.Command);
        Assert.Contains("E_FAIL", ex.Message);
    }

    [Fact]
    public async Task PullWithProgressAsync_Cancellation_IsPropagated()
    {
        var cli = new CancellingCli();
        var service = new ImagesService(cli);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PullWithProgressAsync(BusyBoxRef, null, CancellationToken.None));
        Assert.Equal(new[] { "pull", BusyBoxRef }, cli.LastArguments);
    }
    /// <summary>模拟进程已经启动、但中途被取消(里取消拉取的底层形态)。</summary>
    private sealed class CancellingCli : IWslcCli
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        // 这些闸门假 CLI 只关心"调用有没有挂起",用不到实时上屏,所以伪终端路径同样转给一次性实现。
        public Task<WslcCliResult> RunWithConsoleAsync(IReadOnlyList<string> arguments, Action<IReadOnlyList<string>>? onVisibleLines, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            return Task.FromException<WslcCliResult>(new OperationCanceledException(cancellationToken));
        }
    }
}
