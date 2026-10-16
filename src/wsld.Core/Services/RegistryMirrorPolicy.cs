namespace wsld.Core.Services;

/// <summary>
/// 一次"镜像源改写"的结果(功能 010 · AC2/AC3)。
/// Original 是用户输入的引用,Reference 是真正交给 <c>wslc pull</c> 的引用。
/// </summary>
/// <param name="Original">用户在拉取框里输入(或调用方传入)的原始引用。</param>
/// <param name="Reference">实际要拉取的引用;命中改写规则时才是镜像站地址。</param>
/// <param name="Mirror">规范化后的镜像源地址;未启用时为空串。</param>
/// <param name="Rewritten">是否真的改写过。</param>
public sealed record MirrorRewrite(string Original, string Reference, string Mirror, bool Rewritten)
{
    internal static MirrorRewrite Unchanged(string reference, string mirror = "") => new(reference, reference, mirror, false);

    internal static MirrorRewrite Applied(string original, string rewritten, string mirror) => new(original, rewritten, mirror, true);
}

/// <summary>
/// 镜像源(registry mirror)策略(功能 010)。
///
/// 实测依据:本机 WSL 3.0.1 的 <c>wslc</c> 没有任何镜像加速开关 ——
/// <c>wslc settings</c> 生成的 <c>%LOCALAPPDATA%\wslc\settings.yaml</c> 只有
/// session.*(cpuCount / memorySize / maxStorageSize / storagePath / networkingMode /
/// hostFileShareMode / dnsTunneling / hostLoopback / defaultBindingAddress / idleTimeout)、
/// credentialStore、experimental.portRelay 这些键,与注册表沾边的只有登录凭据;
/// <c>wslc pull</c> 也只有 -a / -q。
/// 所以"配置 registry-mirror"只能做在应用侧:把引用改写成"&lt;镜像站&gt;/&lt;路径&gt;"再交给 <c>wslc pull</c>。
///
/// 改写规则对齐 Docker 的引用规范化:裸名字(<c>nginx</c>)属于 Docker Hub 官方库,补 <c>library/</c>;
/// <c>bitnami/redis</c> 属于 Docker Hub 用户仓库,原样接在镜像站后面;
/// 已经写全域名的(<c>ghcr.io/…</c>)不属于 Docker Hub,不碰。
/// </summary>
public static class RegistryMirrorPolicy
{
    /// <summary>Docker Hub 的几种等价写法,都按"没写主机名"处理。</summary>
    private static readonly string[] DockerHubHosts =
    {
        "docker.io",
        "index.docker.io",
        "registry-1.docker.io",
    };

    /// <summary>Docker Hub 官方镜像的命名空间(功能 010 · AC2)。</summary>
    public const string OfficialNamespace = "library";

    /// <summary>规范化后的镜像源地址;留空或非法返回空串,含义是"不使用镜像源"。</summary>
    public static string Normalize(string? mirror) => TryNormalize(mirror, out var host) ? host : "";

    /// <summary>
    /// 规范化:允许用户连协议一起粘贴(<c>https://docker.1ms.run/</c>);
    /// 主机名不区分大小写,可以带端口或路径前缀;
    /// 带空白、既没有点也没有端口又不是 localhost 的,一律判为无效(功能 010 · AC1)。
    /// </summary>
    public static bool TryNormalize(string? mirror, out string normalized)
    {
        normalized = "";
        var raw = mirror?.Trim() ?? "";
        if (raw.Length == 0) return false;

        var withoutScheme = raw;
        var schemeIndex = withoutScheme.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0) withoutScheme = withoutScheme[(schemeIndex + 3)..];

        withoutScheme = withoutScheme.Trim().TrimEnd('/');
        if (withoutScheme.Length == 0) return false;
        if (withoutScheme.Any(char.IsWhiteSpace)) return false;

        var slash = withoutScheme.IndexOf('/');
        var authority = slash >= 0 ? withoutScheme[..slash] : withoutScheme;
        var path = slash >= 0 ? withoutScheme[slash..] : "";

        if (!IsRegistryHost(authority)) return false;

        normalized = authority.ToLowerInvariant() + path;
        return true;
    }

    /// <summary>按设置的镜像源改写引用(功能 010 · AC2/AC3)。不命中规则时原样返回。</summary>
    public static MirrorRewrite Rewrite(string? reference, string? mirror)
    {
        var original = reference?.Trim() ?? "";
        if (original.Length == 0) return MirrorRewrite.Unchanged(original);
        if (!TryNormalize(mirror, out var host)) return MirrorRewrite.Unchanged(original);

        var (repository, suffix) = SplitParts(original);
        var segments = repository.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return MirrorRewrite.Unchanged(original, host);

        var first = segments[0];

        // 第一段带点、带端口,或者就叫 localhost → 引用里已经写了主机名。
        if (IsRegistryHost(first))
        {
            if (!IsDockerHubHost(first)) return MirrorRewrite.Unchanged(original, host);

            var rest = segments[1..];
            if (rest.Length == 0) return MirrorRewrite.Unchanged(original, host); // 只写了 docker.io,不猜要拉什么
            return MirrorRewrite.Applied(original, host + "/" + QualifyNamespace(rest) + suffix, host);
        }

        // nginx / redis:7 → 官方库;bitnami/redis → 用户仓库。
        return MirrorRewrite.Applied(original, host + "/" + QualifyNamespace(segments) + suffix, host);
    }

    private static string QualifyNamespace(string[] segments) =>
        segments.Length == 1
            ? OfficialNamespace + "/" + segments[0]
            : string.Join('/', segments);

    /// <summary>
    /// 拆出"仓库路径"与后面的 <c>:tag</c> / <c>@digest</c> 尾巴。
    /// 最后一个斜杠之前的冒号属于端口(如 <c>localhost:5000/nginx</c>),不能当标签分隔符。
    /// </summary>
    private static (string Repository, string Suffix) SplitParts(string reference)
    {
        var digestAt = reference.IndexOf('@');
        var namePart = digestAt >= 0 ? reference[..digestAt] : reference;
        var tail = digestAt >= 0 ? reference[digestAt..] : "";

        var lastSlash = namePart.LastIndexOf('/');
        var colon = namePart.IndexOf(':', lastSlash + 1);
        return colon >= 0
            ? (namePart[..colon], namePart[colon..] + tail)
            : (namePart, tail);
    }

    private static bool IsRegistryHost(string candidate) =>
        candidate.Contains('.') || candidate.Contains(':') || candidate.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsDockerHubHost(string candidate) => DockerHubHosts.Contains(candidate.ToLowerInvariant());
}