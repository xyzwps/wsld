using WslcDesktop.Core.Models;
using WslcDesktop.Core.Services;

namespace WslcDesktop.Tests;

/// <summary>功能 012:wslc run 命令的拼装 / 校验 / 读回(纯逻辑,不起进程)。</summary>
public class RunCommandBuilderTests
{
    private static RunSpec FullSpec()
    {
        var spec = new RunSpec
        {
            Image = "nginx:1.27",
            Detach = true,
            Interactive = true,
            Tty = true,
            Rm = true,
            PublishAll = true,
            PullPolicy = "always",
            Name = "web",
            Hostname = "myhost",
            Network = "host",
            Cpus = "2",
            Memory = "512m",
            ShmSize = "64m",
            Workdir = "/app",
            User = "1000:1000",
            EnvFile = ".env",
            Entrypoint = "sh",
            HealthCmd = "curl -f localhost",
            HealthInterval = "30s",
            HealthTimeout = "5s",
            HealthRetries = "3",
            HealthStartPeriod = "10s",
            ExtraArgs = "--gpus all",
            Command = "sleep",
            Arguments = "infinity",
        };
        spec.EnvVars.Add(new EnvRow { Key = "KEY", Value = "val" });
        spec.EnvVars.Add(new EnvRow { Key = "EMPTY", Value = "" });
        spec.Labels.Add(new LabelRow { Key = "app", Value = "web" });
        spec.Ports.Add(new PortRow { HostPort = "8080", ContainerPort = "80" });
        spec.Ports.Add(new PortRow { HostIp = "127.0.0.1", HostPort = "53", ContainerPort = "53", Protocol = "udp" });
        spec.TmpfsPaths.Add(new PathRow { Path = "/tmp" });
        spec.Volumes.Add(new VolumeRow { Source = @"C:\data", Target = "/app", ReadOnly = true });
        return spec;
    }

    [Fact]
    public void Build_FixedOrder_FlagsThenOptionsThenPositionals()
    {
        var args = RunCommandBuilder.Build(FullSpec());

        Assert.Equal(new[]
        {
            "run", "-d", "-i", "-t", "--rm", "-P",
            "--pull", "always", "--name", "web", "-h", "myhost",
            "--network", "host", "--cpus", "2", "-m", "512m", "--shm-size", "64m",
            "-w", "/app", "-u", "1000:1000",
            "-e", "KEY=val", "-e", "EMPTY=", "--env-file", ".env",
            "-l", "app=web",
            "-p", "8080:80", "-p", "127.0.0.1:53:53/udp",
            "--entrypoint", "sh", "--tmpfs", "/tmp", "-v", @"C:\data:/app:ro",
            "--health-cmd", "curl -f localhost", "--health-interval", "30s",
            "--health-timeout", "5s", "--health-retries", "3", "--health-start-period", "10s",
            "--gpus", "all",
            "nginx:1.27", "sleep", "infinity",
        }, args);
    }

    [Fact]
    public void Build_SkipsEmptyRowsAndEmptyOptions()
    {
        var spec = new RunSpec { Image = "x", Name = "  ", Detach = false };
        spec.Ports.Add(new PortRow());
        spec.Volumes.Add(new VolumeRow());
        spec.EnvVars.Add(new EnvRow());
        spec.Labels.Add(new LabelRow());
        spec.TmpfsPaths.Add(new PathRow());

        Assert.Equal(new[] { "run", "x" }, RunCommandBuilder.Build(spec));
    }

    [Fact]
    public void Display_QuotesOnlyTokensThatNeedIt()
    {
        var display = RunCommandBuilder.Display(FullSpec());

        Assert.Equal(
            "wslc run -d -i -t --rm -P --pull always --name web -h myhost " +
            "--network host --cpus 2 -m 512m --shm-size 64m -w /app -u 1000:1000 " +
            "-e KEY=val -e EMPTY= --env-file .env -l app=web " +
            "-p 8080:80 -p 127.0.0.1:53:53/udp --entrypoint sh --tmpfs /tmp -v C:\\data:/app:ro " +
            "--health-cmd \"curl -f localhost\" --health-interval 30s --health-timeout 5s " +
            "--health-retries 3 --health-start-period 10s --gpus all nginx:1.27 sleep infinity",
            display);
    }

    [Fact]
    public void Display_IsBuiltArgsTokenForToken_PreviewEqualsExecution()
    {
        var spec = FullSpec();
        var expected = new[] { "wslc" }.Concat(RunCommandBuilder.Build(spec));

        Assert.Equal(expected, RunCommandBuilder.Tokenize(RunCommandBuilder.Display(spec)));
    }

    [Fact]
    public void Validate_BareSpec_OnlyComplaintIsMissingImage()
    {
        var issue = Assert.Single(RunCommandBuilder.Validate(new RunSpec()));

        Assert.Equal(RunIssueCode.ImageMissing, issue.Code);
    }

    [Fact]
    public void Validate_PortRows_ContainerMissingNumberRangeMismatchAndProtocol()
    {
        var spec = new RunSpec { Image = "x" };
        spec.Ports.Add(new PortRow { HostPort = "8080" });                         // 1: 缺容器端口
        spec.Ports.Add(new PortRow { ContainerPort = "abc" });                     // 2: 不是端口
        spec.Ports.Add(new PortRow { HostPort = "70000", ContainerPort = "80" });  // 3: 超范围
        spec.Ports.Add(new PortRow { HostPort = "8000-8010", ContainerPort = "80" }); // 4: 一端范围一端单口
        spec.Ports.Add(new PortRow { HostPort = "9000-9010", ContainerPort = "8000-8010" }); // 5: 等长范围,合法
        spec.Ports.Add(new PortRow { ContainerPort = "80", Protocol = "sctp" });   // 6: 协议不认

        var issues = RunCommandBuilder.Validate(spec);

        Assert.Equal(
            new (RunIssueCode Code, int? Row)[]
            {
                (RunIssueCode.PortContainerMissing, 1),
                (RunIssueCode.PortNumber, 2),
                (RunIssueCode.PortNumber, 3),
                (RunIssueCode.PortRange, 4),
                (RunIssueCode.PortProtocol, 6),
            },
            issues.Select(i => (i.Code, i.Row)));
        Assert.Equal("abc", issues[1].Value);
        Assert.Equal("70000", issues[2].Value);
    }

    [Fact]
    public void Validate_PortsAcceptSingleNumbersAndValidRanges()
    {
        var spec = new RunSpec { Image = "x" };
        spec.Ports.Add(new PortRow { HostPort = "8080", ContainerPort = "80" });
        spec.Ports.Add(new PortRow { HostPort = "8000-8010", ContainerPort = "8000-8010" });
        spec.Ports.Add(new PortRow { HostPort = "1", ContainerPort = "65535" });

        Assert.Empty(RunCommandBuilder.Validate(spec));
    }

    [Fact]
    public void Validate_PortZeroAndDescendingRange_AreNotPorts()
    {
        var spec = new RunSpec { Image = "x" };
        spec.Ports.Add(new PortRow { ContainerPort = "0" });
        spec.Ports.Add(new PortRow { ContainerPort = "90-80" });

        var issues = RunCommandBuilder.Validate(spec);

        Assert.Equal(2, issues.Count);
        Assert.All(issues, issue => Assert.Equal(RunIssueCode.PortNumber, issue.Code));
    }

    [Fact]
    public void Validate_VolumesTmpfsEnvLabel_FlagTheirRespectiveRows()
    {
        var spec = new RunSpec { Image = "x" };
        spec.Volumes.Add(new VolumeRow { Target = "/app" });                    // 缺来源
        spec.Volumes.Add(new VolumeRow { Source = "data", Target = "app" });    // 目标不以 / 开头
        spec.TmpfsPaths.Add(new PathRow { Path = "tmp" });
        spec.EnvVars.Add(new EnvRow { Value = "orphan" });                      // 有值无名
        spec.EnvVars.Add(new EnvRow { Key = "A=B" });                           // 名字带 =
        spec.Labels.Add(new LabelRow { Key = "k=v" });

        var issues = RunCommandBuilder.Validate(spec);

        Assert.Equal(
            new (RunIssueCode Code, int? Row)[]
            {
                (RunIssueCode.VolumeSource, 1),
                (RunIssueCode.VolumeTarget, 2),
                (RunIssueCode.TmpfsPath, 1),
                (RunIssueCode.EnvKey, 1),
                (RunIssueCode.EnvKey, 2),
                (RunIssueCode.LabelKey, 1),
            },
            issues.Select(i => (i.Code, i.Row)));
    }

    [Fact]
    public void Validate_HealthRetriesAndCpus_MustBeNumbers()
    {
        var spec = new RunSpec { Image = "x", HealthRetries = "abc", Cpus = "0" };
        Assert.Contains(RunIssueCode.HealthRetries, RunCommandBuilder.Validate(spec).Select(i => i.Code));
        Assert.Contains(RunIssueCode.Cpus, RunCommandBuilder.Validate(spec).Select(i => i.Code));

        spec.HealthRetries = "-1";
        spec.Cpus = "-2.5";
        var issues = RunCommandBuilder.Validate(spec);
        Assert.Contains(RunIssueCode.HealthRetries, issues.Select(i => i.Code));
        Assert.Contains(RunIssueCode.Cpus, issues.Select(i => i.Code));

        spec.HealthRetries = "3";
        spec.Cpus = "0.5";
        Assert.Empty(RunCommandBuilder.Validate(spec));
    }

    [Fact]
    public void Tokenize_HandlesQuotingBackslashesAndSingleQuoteSpans()
    {
        Assert.Equal(new[] { "a", "b", "c" }, RunCommandBuilder.Tokenize("a  b\tc"));
        Assert.Equal(new[] { "hello world", "x" }, RunCommandBuilder.Tokenize("\"hello world\" x"));
        Assert.Equal(new[] { @"C:\data\" }, RunCommandBuilder.Tokenize(@"C:\data\"));
        Assert.Equal(new[] { @"a\b" }, RunCommandBuilder.Tokenize("\"a\\b\""));
        Assert.Equal(new[] { "a b", "c" }, RunCommandBuilder.Tokenize("'a b' c"));
        Assert.Equal(new[] { "say \"hi\"" }, RunCommandBuilder.Tokenize("\"say \\\"hi\\\"\""));
        Assert.Empty(RunCommandBuilder.Tokenize(null));
        Assert.Empty(RunCommandBuilder.Tokenize(""));
    }

    [Fact]
    public void Quote_WrapsSpacesAndShellControlCharactersOnly()
    {
        Assert.Equal("plain", RunCommandBuilder.Quote("plain"));
        Assert.Equal(@"C:\data:/app:ro", RunCommandBuilder.Quote(@"C:\data:/app:ro"));
        Assert.Equal("\"a b\"", RunCommandBuilder.Quote("a b"));
        Assert.Equal("\"a;b\"", RunCommandBuilder.Quote("a;b"));
        Assert.Equal("\"x&y\"", RunCommandBuilder.Quote("x&y"));
        Assert.Equal("\"\"", RunCommandBuilder.Quote(""));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("a b")]
    [InlineData(@"C:\data\")]
    [InlineData("say \"hi\"")]
    [InlineData("a;b&c|d(<x>)^$`")]
    [InlineData("it's")]
    [InlineData("")]
    public void QuoteThenTokenize_IsIdentity(string token)
    {
        Assert.Equal(new[] { token }, RunCommandBuilder.Tokenize(RunCommandBuilder.Quote(token)));
    }

    [Theory]
    [InlineData("wslc run -d nginx")]
    [InlineData("wslc.exe run -d nginx")]
    [InlineData(@"C:\tools\wslc.exe run -d nginx")]
    [InlineData(@"c:\Tools\WSLC.EXE container run -d nginx")]
    public void TryParse_AcceptsWslcPrefixVariants(string commandLine)
    {
        Assert.True(RunCommandBuilder.TryParse(commandLine, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal("nginx", parsed.Image);
        Assert.True(parsed.Detach);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("docker run nginx")]
    [InlineData("wslc")]
    [InlineData("wslc ps -a")]
    public void TryParse_RejectsNonRunCommands(string? commandLine)
    {
        Assert.False(RunCommandBuilder.TryParse(commandLine, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_KnowsPortsVolumesEnvAndPullPolicy()
    {
        Assert.True(RunCommandBuilder.TryParse(
            "wslc run -d --name web -p 8080:80/udp -p 127.0.0.1:53:53 " +
            @"-v C:\data:/app:ro -e MODE=fast --pull never nginx", out var parsed));
        var spec = parsed!;

        Assert.Equal("web", spec.Name);
        Assert.Equal("never", spec.PullPolicy);
        Assert.Equal(2, spec.Ports.Count);
        Assert.Equal("udp", spec.Ports[0].Protocol);
        Assert.Equal("8080", spec.Ports[0].HostPort);
        Assert.Equal("80", spec.Ports[0].ContainerPort);
        Assert.Equal("127.0.0.1", spec.Ports[1].HostIp);
        Assert.Equal("tcp", spec.Ports[1].Protocol);
        var volume = Assert.Single(spec.Volumes);
        Assert.Equal(@"C:\data", volume.Source);
        Assert.Equal("/app", volume.Target);
        Assert.True(volume.ReadOnly);
        var env = Assert.Single(spec.EnvVars);
        Assert.Equal("MODE", env.Key);
        Assert.Equal("fast", env.Value);
    }

    [Fact]
    public void TryParse_QuotedEnvValueSurvivesSpaces()
    {
        Assert.True(RunCommandBuilder.TryParse("wslc run -e \"A=b c\" nginx", out var parsed));

        var env = Assert.Single(parsed!.EnvVars);
        Assert.Equal("A", env.Key);
        Assert.Equal("b c", env.Value);
    }

    [Fact]
    public void TryParse_UnknownPullPolicy_GoesToExtraArgsToKeepPreviewHonest()
    {
        Assert.True(RunCommandBuilder.TryParse("wslc run --pull if-not-present nginx", out var parsed));

        Assert.Equal("", parsed!.PullPolicy);
        Assert.Equal("--pull if-not-present", parsed.ExtraArgs);
        Assert.Contains("--pull if-not-present", RunCommandBuilder.Display(parsed));
    }

    [Fact]
    public void TryParse_PassesUnknownValueFlagsIntoExtraArgsVerbatim()
    {
        Assert.True(RunCommandBuilder.TryParse(
            "wslc run --gpus all --dns 8.8.8.8 --ulimit nofile=1024:2048 nginx", out var parsed));

        Assert.Equal("nginx", parsed!.Image);
        Assert.Equal("--gpus all --dns 8.8.8.8 --ulimit nofile=1024:2048", parsed.ExtraArgs);
    }

    [Fact]
    public void TryParse_DisplayedCommand_RoundTripsToTheSameCommand()
    {
        var display = RunCommandBuilder.Display(FullSpec());

        Assert.True(RunCommandBuilder.TryParse(display, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(display, RunCommandBuilder.Display(parsed));
    }

    [Fact]
    public async Task ContainersService_RunAsync_PassesBuiltArgsThroughStreamingPath()
    {
        var cli = new RoutingWslcCli();
        cli.EnqueueStream(0, "abc123\n");
        var service = new ContainersService(cli);
        var spec = new RunSpec { Image = "hello-world" };
        spec.Ports.Add(new PortRow { HostPort = "8080", ContainerPort = "80" });

        var lines = new List<string>();
        var result = await service.RunAsync(spec, lines.Add);

        Assert.Equal(RunCommandBuilder.Build(spec), cli.LastStreamCall("run"));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new[] { "abc123" }, lines);
    }
}