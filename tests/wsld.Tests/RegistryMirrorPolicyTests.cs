using wsld.Core.Services;

namespace wsld.Tests;

/// <summary>
/// 功能 010 · AC1/AC2/AC3:镜像源地址的规范化,以及"把引用改写成镜像站地址"的规则。
/// 规则对齐 Docker 的引用规范化:裸名字算 Docker Hub 官方库(补 library/),
/// 已经写全域名的非 Hub 仓库一律不碰。
/// </summary>
public class RegistryMirrorPolicyTests
{
    private const string Mirror = "docker.1ms.run";

    // ---------- AC1 · 地址规范化 ----------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("docker.1ms.run", "docker.1ms.run")]
    [InlineData("  docker.1ms.run  ", "docker.1ms.run")]
    [InlineData("https://docker.1ms.run", "docker.1ms.run")]
    [InlineData("http://docker.1ms.run/", "docker.1ms.run")]
    [InlineData("https://docker.1ms.run///", "docker.1ms.run")]
    [InlineData("HTTPS://Docker.1MS.RUN", "docker.1ms.run")]
    [InlineData("localhost", "localhost")]
    [InlineData("localhost:5000", "localhost:5000")]
    [InlineData("LOCALHOST:5000", "localhost:5000")]
    [InlineData("registry.example.com/mirror", "registry.example.com/mirror")]
    [InlineData("registry.example.com:443/a/b/", "registry.example.com:443/a/b")]
    public void Normalize_AcceptsWhatUsersPaste(string? raw, string expected) =>
        Assert.Equal(expected, RegistryMirrorPolicy.Normalize(raw));

    [Theory]
    [InlineData("example")]           // 既没有点也没有端口,不像是注册地址
    [InlineData("docker 1ms.run")]    // 中间有空格
    [InlineData("://")]
    [InlineData("https://")]
    [InlineData("/")]
    [InlineData("   /   ")]
    public void Normalize_RejectsNonAddresses(string raw) =>
        Assert.Equal("", RegistryMirrorPolicy.Normalize(raw));

    [Fact]
    public void TryNormalize_TellsTheSettingsPageWhetherTheInputIsUsable()
    {
        Assert.True(RegistryMirrorPolicy.TryNormalize(" https://Docker.1ms.run/ ", out var good));
        Assert.Equal("docker.1ms.run", good);

        Assert.False(RegistryMirrorPolicy.TryNormalize("不像地址", out var bad));
        Assert.Equal("", bad);
    }

    [Fact]
    public void Normalize_LowercasesHostButKeepsPathPrefixAsIs()
    {
        // 主机名不区分大小写;路径前缀可能区分,原样留着。
        Assert.Equal("registry.example.com/Mirror", RegistryMirrorPolicy.Normalize("https://Registry.Example.com/Mirror"));
    }

    // ---------- AC2 · Docker Hub 的名字走镜像站 ----------

    [Theory]
    [InlineData("redis:7", Mirror + "/library/redis:7")]
    [InlineData("nginx", Mirror + "/library/nginx")]
    [InlineData("nginx:latest", Mirror + "/library/nginx:latest")]
    [InlineData("library/nginx:latest", Mirror + "/library/nginx:latest")]
    [InlineData("bitnami/redis", Mirror + "/bitnami/redis")]
    [InlineData("bitnami/redis:7.2-alpine", Mirror + "/bitnami/redis:7.2-alpine")]
    [InlineData("docker.io/nginx", Mirror + "/library/nginx")]
    [InlineData("docker.io/nginx:1.27", Mirror + "/library/nginx:1.27")]
    [InlineData("Docker.io/bitnami/redis", Mirror + "/bitnami/redis")]
    [InlineData("index.docker.io/library/alpine:3.20", Mirror + "/library/alpine:3.20")]
    [InlineData("registry-1.docker.io/alpine:3.20", Mirror + "/library/alpine:3.20")]
    [InlineData("  redis:7  ", Mirror + "/library/redis:7")]
    public void Rewrite_DockerHubReferences_GoesThroughMirror(string reference, string expected)
    {
        var result = RegistryMirrorPolicy.Rewrite(reference, Mirror);

        Assert.True(result.Rewritten);
        Assert.Equal(expected, result.Reference);
        Assert.Equal(Mirror, result.Mirror);
    }

    [Fact]
    public void Rewrite_KeepsBothTheOriginalAndTheEffectiveReference()
    {
        // 界面提示与改名都要用回用户写的那个名字。
        var result = RegistryMirrorPolicy.Rewrite("redis:7", "https://docker.1ms.run/");

        Assert.Equal("redis:7", result.Original);
        Assert.Equal("docker.1ms.run/library/redis:7", result.Reference);
    }

    [Fact]
    public void Rewrite_WithPortOrPathPrefixMirror_KeepsBoth()
    {
        Assert.Equal("localhost:5000/library/redis:7", RegistryMirrorPolicy.Rewrite("redis:7", "localhost:5000").Reference);
        Assert.Equal(
            "registry.example.com/mirror/library/nginx",
            RegistryMirrorPolicy.Rewrite("nginx", "https://Registry.Example.com/mirror/").Reference);
    }

    [Fact]
    public void OfficialNamespace_IsLibrary() => Assert.Equal("library", RegistryMirrorPolicy.OfficialNamespace);

    // ---------- AC3 · 别的注册表和已经写全的名字不碰 ----------

    [Theory]
    [InlineData("ghcr.io/org/app:1")]
    [InlineData("quay.io/prometheus/node-exporter:v1.8.2")]
    [InlineData("mcr.microsoft.com/dotnet/runtime:8.0")]
    [InlineData("localhost:5000/my-app:dev")]
    [InlineData("registry.example.com:5000/team/tool")]
    [InlineData("ghcr.io")]                                        // 只写了主机名
    [InlineData(Mirror + "/library/redis:7")]                     // 已经写了镜像站全名
    [InlineData("docker.io")]                                     // 只写 Hub 域名,不猜要拉什么
    [InlineData("index.docker.io")]
    [InlineData("")]
    [InlineData("   ")]
    public void Rewrite_LeavesThoseReferencesExactlyAsTyped(string reference)
    {
        var result = RegistryMirrorPolicy.Rewrite(reference, Mirror);

        Assert.False(result.Rewritten);
        Assert.Equal(reference.Trim(), result.Reference);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a host")]
    [InlineData("https://")]
    public void Rewrite_WithoutUsableMirror_PullsExactlyWhatWasTyped(string mirror)
    {
        var result = RegistryMirrorPolicy.Rewrite("redis:7", mirror);

        Assert.False(result.Rewritten);
        Assert.Equal("redis:7", result.Reference);
        Assert.Equal("", result.Mirror);
    }

    // ---------- :tag 与 @digest 的尾巴必须原样跟着 ----------

    [Theory]
    [InlineData("alpine@sha256:aaaa", Mirror + "/library/alpine@sha256:aaaa")]
    [InlineData("bitnami/redis@sha256:bbbb", Mirror + "/bitnami/redis@sha256:bbbb")]
    [InlineData("redis:7@sha256:cccc", Mirror + "/library/redis:7@sha256:cccc")]
    [InlineData("docker.io/library/nginx:1.27@sha256:dddd", Mirror + "/library/nginx:1.27@sha256:dddd")]
    [InlineData("ghcr.io/org/app@sha256:eeee", "ghcr.io/org/app@sha256:eeee")]
    public void Rewrite_KeepsTagAndDigestTail(string reference, string expected)
    {
        var result = RegistryMirrorPolicy.Rewrite(reference, Mirror);

        Assert.Equal(expected, result.Reference);
        Assert.Equal(result.Rewritten, !string.Equals(reference, expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Rewrite_ColonInsideAHostIsNotMistakenForATagSeparator()
    {
        // localhost:5000/redis 里的冒号属于端口;补的是镜像站前缀,不是 :latest。
        var result = RegistryMirrorPolicy.Rewrite("localhost:5000/redis", Mirror);

        Assert.False(result.Rewritten);
        Assert.Equal("localhost:5000/redis", result.Reference);
    }
}