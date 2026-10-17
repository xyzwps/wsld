using wsld.Core.Parsing;

namespace wsld.Tests;

/// <summary>册子 CL-002、CL-003、CL-004、CL-016、CL-017、CD-007:容器 JSON 的排版兼容性与字段映射。</summary>
public class WslcJsonParserTests
{
    [Fact]
    [Trait("case", "CL-004")]
    public void EmptyOutput_ReturnsEmptyList_NotError()
    {
        var items = WslcJsonParser.ParseContainers("");
        Assert.Empty(items);
    }

    [Theory]
    [Trait("case", "CL-004")]
    [InlineData("\r\n")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void WhitespaceOnly_ReturnsEmptyList(string output)
    {
        Assert.Empty(WslcJsonParser.ParseContainers(output));
    }

    [Fact]
    [Trait("case", "CL-016")]
    public void SingleObject_IsSupported()
    {
        var items = WslcJsonParser.ParseContainers("""{"ID":"abc","Names":"one"}""");
        var item = Assert.Single(items);
        Assert.Equal("one", item.DisplayName);
    }

    [Fact]
    [Trait("case", "CL-016")]
    [Trait("case", "CD-007")]
    public void NdjsonMultipleLines_MapRealFields()
    {
        // 与本机 wslc 3.0.1 输出一致的样例(Fixtures 与内联两条)。
        var text = FixtureLoader.Read("wslc-list-running.ndjson").Trim();
        var items = WslcJsonParser.ParseContainers(text);

        var probe = Assert.Single(items);
        Assert.Equal("f47fdddf9219", probe.Id);
        Assert.Equal("probe1", probe.DisplayName);
        Assert.Equal("docker.1ms.run/library/busybox:latest", probe.Image);
        Assert.Equal("running", probe.State);
        Assert.True(probe.IsRunning);
        Assert.Equal("\"sleep 3600\"", probe.Command);
        Assert.Equal("bridge", probe.Networks);
        Assert.NotNull(probe.Platform);
        Assert.Equal("amd64", probe.Platform!.Architecture);
        Assert.Equal("linux", probe.Platform.OS);
    }

    [Fact]
    [Trait("case", "CL-016")]
    public void TwoNdjsonLines_ReturnsTwoContainers()
    {
        var text = FixtureLoader.Read("wslc-list-exited.ndjson").Trim()
            + "\n" + FixtureLoader.Read("wslc-list-running.ndjson").Trim();
        var items = WslcJsonParser.ParseContainers(text);
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Id)));
    }

    [Fact]
    [Trait("case", "CL-003")]
    public void ExitedState_IsNotRunning()
    {
        var items = WslcJsonParser.ParseContainers(FixtureLoader.Read("wslc-list-exited.ndjson"));
        var probe = Assert.Single(items);
        Assert.False(probe.IsRunning);
        Assert.Equal("exited", probe.State);
    }

    [Fact]
    [Trait("case", "CL-016")]
    public void JsonArray_IsSupported()
    {
        var text = """[{"ID":"a1","Names":"x","State":"running"},{"ID":"b2","Names":"y","State":"exited"}]""";
        var items = WslcJsonParser.ParseContainers(text);
        Assert.Equal(2, items.Count);
        Assert.True(items[0].IsRunning);
        Assert.False(items[1].IsRunning);
    }

    [Fact]
    [Trait("case", "CL-016")]
    public void PrettyPrintedJson_IsSupported()
    {
        var text = """
            {
              "ID": "pretty1",
              "Names": "pretty",
              "State": "running",
              "Image": "img"
            }
            """;
        var items = WslcJsonParser.ParseContainers(text);
        var item = Assert.Single(items);
        Assert.Equal("pretty1", item.Id);
        Assert.True(item.IsRunning);
    }

    [Fact]
    [Trait("case", "CL-017")]
    public void BadLine_ReportsLineNumberAndSnippet()
    {
        var text = """
            {"ID":"ok1","Names":"good"}
            这不是 JSON
            """;
        var ex = Assert.Throws<FormatException>(() => WslcJsonParser.ParseContainers(text));
        Assert.Contains("第 2 行", ex.Message);
        Assert.Contains("这不是 JSON", ex.Message);
    }

    [Fact]
    [Trait("case", "CL-002")]
    public void DisplayName_StripsLeadingSlashAndTakesFirstName()
    {
        var items = WslcJsonParser.ParseContainers("""{"ID":"z","Names":"/web,alias1"}""");
        Assert.Equal("web", Assert.Single(items).DisplayName);
    }
}
