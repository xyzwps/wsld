using WslcDesktop.Core.I18n;

namespace WslcDesktop.Core.Settings;

/// <summary>用户可改的应用设置;新增字段一律给默认值,老配置文件才能继续读。</summary>
public sealed class AppSettings
{
    private string _registryMirror = "";

    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;

    public AppLanguage Language { get; set; } = AppLanguage.FollowSystem;

    /// <summary>
    /// 镜像源(registry mirror)地址,功能 010 · AC1。
    /// 空串 = 不使用;容忍用户连 <c>https://</c> 一起粘贴,规范化在
    /// <see cref="WslcDesktop.Core.Services.RegistryMirrorPolicy"/> 里做。
    /// setter 归一化:配置文件里写成 <c>"RegistryMirror": null</c> 时不能把 null 放进来(开发笔记 #9)。
    /// </summary>
    public string RegistryMirror
    {
        get => _registryMirror;
        set => _registryMirror = value ?? "";
    }

    /// <summary>
    /// 走镜像源拉下来之后,是否把镜像改名回用户输入的名字(功能 010 · AC5)。
    /// 默认开:列表里出现 <c>redis:7</c> 而不是 <c>docker.1ms.run/library/redis:7</c>,
    /// 和用户在容器页里想写的名字一致。
    /// </summary>
    public bool RenameMirrorPull { get; set; } = true;
}