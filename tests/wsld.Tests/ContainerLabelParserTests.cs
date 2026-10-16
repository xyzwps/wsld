using wsld.Core.Parsing;

namespace wsld.Tests;

/// <summary>容器标签串的拆分:wslc 只给一条逗号拼接的串,而值里就带逗号。</summary>
public class ContainerLabelParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空串_不产生标签(string? raw)
    {
        Assert.Empty(ContainerLabelParser.Parse(raw));
    }

    [Fact]
    public void 普通多标签_按逗号拆开()
    {
        var labels = ContainerLabelParser.Parse("a=1,org.opencontainers.image.title=Foo,wslcd=test");

        Assert.Equal(3, labels.Count);
        Assert.Equal(("a", "1"), (labels[0].Key, labels[0].Value));
        Assert.Equal(("org.opencontainers.image.title", "Foo"), (labels[1].Key, labels[1].Value));
        Assert.Equal(("wslcd", "test"), (labels[2].Key, labels[2].Value));
    }

    [Fact]
    public void 最后一条标签_键值都在()
    {
        var labels = ContainerLabelParser.Parse("a=1,wslcd=test");
        Assert.Equal(("wslcd", "test"), (labels[^1].Key, labels[^1].Value));
    }

    [Fact]
    public void 值里的等号与URL_原样保留()
    {
        var labels = ContainerLabelParser.Parse("url=https://x.io/?a=b&c=d,org.opencontainers.image.source=https://github.com/o/r");

        Assert.Equal(2, labels.Count);
        Assert.Equal("url", labels[0].Key);
        Assert.Equal("https://x.io/?a=b&c=d", labels[0].Value);
        Assert.Equal("https://github.com/o/r", labels[1].Value);
    }

    [Fact]
    public void 值里的逗号_后面不像标签名就不拆()
    {
        var labels = ContainerLabelParser.Parse("desc=hello, world,org.opencontainers.image.created=2026");

        Assert.Equal(2, labels.Count);
        Assert.Equal("hello, world", labels[0].Value);
        Assert.Equal("org.opencontainers.image.created", labels[1].Key);
    }

    [Fact]
    public void 镜像元数据里的JSON_整串算一个值()
    {
        // 真机样例:com.microsoft.wsl.container.metadata 的值是一坨带逗号的 JSON。
        var raw = """com.microsoft.wsl.container.metadata={"V1":{"Flags":0,"InitProcessFlags":0,"Ports":[{"BindingAddress":"127.0.0.1","ContainerPort":2222,"HostPort":22222,"Protocol":6}],"Volumes":[]}},wslcd=test""";

        var labels = ContainerLabelParser.Parse(raw);

        Assert.Equal(2, labels.Count);
        Assert.Equal("com.microsoft.wsl.container.metadata", labels[0].Key);
        Assert.StartsWith("{\"V1\":{\"Flags\":0", labels[0].Value, StringComparison.Ordinal);
        Assert.EndsWith("\"Volumes\":[]}}", labels[0].Value, StringComparison.Ordinal);
        Assert.Equal(("wslcd", "test"), (labels[1].Key, labels[1].Value));
    }

    [Fact]
    public void 引号内的逗号与转义引号_不会错算边界()
    {
        // 值原样保留(连引号与反斜杠一起),这里只保证引号里那个逗号没被当成边界。
        var labels = ContainerLabelParser.Parse("""meta="a\"b,x",wslcd=test""");

        Assert.Equal(2, labels.Count);
        Assert.Equal("meta", labels[0].Key);
        Assert.Equal("\"a\\\"b,x\"", labels[0].Value);   // 值原样带引号与转义,不替 wslc 猜格式
        Assert.Equal("wslcd", labels[1].Key);
    }

    [Fact]
    public void 描述里带方括号与句号_真边界照样拆得开()
    {
        // 真机样例:描述是一段英文句子,里面既有逗号又有 [..](..) 这类括号。
        var raw = "org.opencontainers.image.description=[Openssh-server](https://www.openssh.com/) is a sandboxed environment. The users only have access to the folders.,org.opencontainers.image.documentation=https://docs.linuxserver.io";

        var labels = ContainerLabelParser.Parse(raw);

        Assert.Equal(2, labels.Count);
        Assert.Equal("org.opencontainers.image.description", labels[0].Key);
        Assert.EndsWith("The users only have access to the folders.", labels[0].Value, StringComparison.Ordinal);
        Assert.Equal("org.opencontainers.image.documentation", labels[1].Key);
    }

    [Fact]
    public void 整串没有等号_原样留成一条不丢数据()
    {
        var labels = ContainerLabelParser.Parse("garbage without equals, but still text");

        var only = Assert.Single(labels);
        Assert.False(only.HasKey);
        Assert.Equal("garbage without equals, but still text", only.Value);
    }

    [Fact]
    public void 键值两端空白_收干净()
    {
        var labels = ContainerLabelParser.Parse("  a = 1 , b=2 ");

        Assert.Equal(2, labels.Count);
        Assert.Equal(("a", "1"), (labels[0].Key, labels[0].Value));
        Assert.Equal(("b", "2"), (labels[1].Key, labels[1].Value));
    }
}
