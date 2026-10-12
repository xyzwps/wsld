using WslcDesktop.Core.Services;
using WslcDesktop.Core.WslcCli;

namespace WslcDesktop.Tests;

/// <summary>功能 004 · AC1–AC4:镜像服务的命令拼装、输入校验、结果解析与错误传播。</summary>
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

    // ---------- AC1 · 拉取命令映射 ----------

    [Fact]
    public async Task PullAsync_InvokesWslcPullWithTrimmedReference()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var service = new ImagesService(cli);

        await service.PullAsync($"  {BusyBoxRef}  ");

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "pull", BusyBoxRef }, args);
    }

    [Fact]
    public async Task PullAsync_UsesDetailedOutputNotQuietFlag()
    {
        // AC1:实测 -q 会把 Status: 行吃掉,拿不到"新下载/已最新"的区分,所以必须用详细输出。
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-new.txt"));
        var service = new ImagesService(cli);

        await service.PullAsync("docker.1ms.run/library/nginx:latest");

        Assert.DoesNotContain("-q", cli.Calls.Single());
    }

    // ---------- AC3 · 拉取结果解析 ----------

    [Fact]
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
    public async Task PullAsync_UpToDateOutput_ReportsUpToDate()
    {
        var cli = new FakeWslcCli().Enqueue(0, FixtureLoader.Read("wslc-pull-uptodate.txt"));
        var service = new ImagesService(cli);

        var result = await service.PullAsync(BusyBoxRef);

        Assert.True(result.IsUpToDate);
        Assert.Equal("docker.1ms.run/library/busybox:latest 已是最新", result.Notice);
    }

    [Fact]
    public async Task PullAsync_OutputWithoutStatusLine_DoesNotThrow()
    {
        // AC3:未来输出形态变化(或改用 -q)时不能崩,按"不是最新"处理即可。
        var cli = new FakeWslcCli().Enqueue(0, BusyBoxRef);
        var service = new ImagesService(cli);

        var result = await service.PullAsync(BusyBoxRef);

        Assert.False(result.IsUpToDate);
        Assert.Equal("", result.StatusLine);
    }

    [Fact]
    public async Task PullAsync_EmptyOutput_DoesNotThrow()
    {
        var cli = new FakeWslcCli().Enqueue(0, "");
        var service = new ImagesService(cli);

        var result = await service.PullAsync(BusyBoxRef);

        Assert.Equal(BusyBoxRef, result.Reference);
        Assert.False(result.IsUpToDate);
    }

    // ---------- AC2 · 空引用绝不启动进程 ----------

    [Theory]
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

    // ---------- AC4 · 删除命令映射 ----------

    [Fact]
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
    public async Task RemoveAsync_Force_AddsFlagBeforeReference()
    {
        var cli = new FakeWslcCli().Enqueue(0, "已删除: sha256:aaef90e06523");
        var service = new ImagesService(cli);

        await service.RemoveAsync(BusyBoxRef, force: true);

        var args = Assert.Single(cli.Calls);
        Assert.Equal(new[] { "rmi", "-f", BusyBoxRef }, args);
    }

    [Fact]
    public async Task RemoveAsync_TrimsReference()
    {
        var cli = new FakeWslcCli().Enqueue(0);
        var service = new ImagesService(cli);

        await service.RemoveAsync($"  {BusyBoxRef}\t");

        Assert.Equal(new[] { "rmi", BusyBoxRef }, Assert.Single(cli.Calls));
    }

    // ---------- 错误传播 ----------

    [Fact]
    public async Task PullAsync_NonZeroExit_ThrowsWithPullCommandAndErrorText()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", PullFailureStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.PullAsync("nginx:latest"));

        Assert.Equal("wslc pull", ex.Command);
        Assert.Contains("E_FAIL", ex.Message);
    }

    [Fact]
    public async Task RemoveAsync_ConflictExit_ThrowsWithRmiCommandAndIsRecognisedAsConflict()
    {
        var cli = new FakeWslcCli().Enqueue(1, "", ConflictStdErr);
        var service = new ImagesService(cli);

        var ex = await Assert.ThrowsAsync<WslcCliException>(() => service.RemoveAsync(BusyBoxRef));

        Assert.Equal("wslc rmi", ex.Command);
        Assert.True(ImageRemovalConflict.IsConflict(ex));
    }

    [Fact]
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

    /// <summary>模拟进程已经启动、但中途被取消(AC6 里取消拉取的底层形态)。</summary>
    private sealed class CancellingCli : IWslcCli
    {
        public IReadOnlyList<string>? LastArguments { get; private set; }

        public Task<WslcCliResult> RunLinesAsync(IReadOnlyList<string> arguments, Action<string>? onStdoutLine, CancellationToken cancellationToken = default) =>
            RunAsync(arguments, cancellationToken);

        public Task<WslcCliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            return Task.FromException<WslcCliResult>(new OperationCanceledException(cancellationToken));
        }
    }
}