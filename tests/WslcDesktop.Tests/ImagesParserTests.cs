using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Tests;

/// <summary>功能 003 · AC2/AC3/AC4:镜像 NDJSON 解析。</summary>
public class ImagesParserTests
{
    [Fact]
    public void RealOutput_MapsAllFields()
    {
        var items = WslcJsonParser.ParseImages(FixtureLoader.Read("wslc-images.ndjson"));

        Assert.Equal(2, items.Count);
        var busybox = items[0];
        Assert.Equal("aaef90e06523", busybox.Id);
        Assert.Equal("docker.1ms.run/library/busybox", busybox.Repository);
        Assert.Equal("latest", busybox.Tag);
        Assert.Equal("4.45MB", busybox.Size);
        Assert.Equal("2026-05-13 10:21:49 +0800 GMT+8", busybox.CreatedAt);
        Assert.Equal("5 months ago", busybox.CreatedSince);
        Assert.Equal("<none>", busybox.Digest);
        Assert.Equal("N/A", busybox.SharedSize);
        Assert.Equal(2, busybox.ContainerCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("   ")]
    public void EmptyOrWhitespace_ReturnsEmptyList(string output)
    {
        Assert.Empty(WslcJsonParser.ParseImages(output));
    }

    [Fact]
    public void JsonArray_IsSupported()
    {
        var text = """[{"ID":"a1","Repository":"redis","Tag":"7"},{"ID":"b2","Repository":"nginx","Tag":"latest"}]""";
        var items = WslcJsonParser.ParseImages(text);
        Assert.Equal(2, items.Count);
        Assert.Equal("redis:7", items[0].Reference);
    }

    [Fact]
    public void SinglePrettyPrintedObject_IsSupported()
    {
        var text = """
            {
              "ID": "prettyimg",
              "Repository": "ghcr.io/demo/app",
              "Tag": "v1",
              "Size": "120MB"
            }
            """;
        var items = WslcJsonParser.ParseImages(text);
        var item = Assert.Single(items);
        Assert.Equal("ghcr.io/demo/app:v1", item.Reference);
        Assert.Equal("120MB", item.SizeText);
    }

    [Fact]
    public void MissingFields_DefaultToEmptyString_NoThrow()
    {
        var items = WslcJsonParser.ParseImages("""{"ID":"only-id"}""");
        var item = Assert.Single(items);
        Assert.Equal("only-id", item.Id);
        Assert.Equal("", item.Repository);
        Assert.False(item.IsTagged);
        Assert.Equal(0, item.ContainerCount);
    }

    [Theory]
    [InlineData("""{"ID":"x","Containers":"3"}""", 3)]
    [InlineData("""{"ID":"x","Containers":3}""", 3)]
    [InlineData("""{"ID":"x","Containers":0}""", 0)]
    [InlineData("""{"ID":"x","Containers":"N/A"}""", 0)]
    [InlineData("""{"ID":"x","Containers":""}""", 0)]
    [InlineData("""{"ID":"x","Containers":null}""", 0)]
    public void ContainersField_AcceptsStringAndNumber(string json, int expected)
    {
        // AC3:wslc 3.0.1 用字符串给数字,未来若改成 JSON 数字也必须读得懂。
        var item = Assert.Single(WslcJsonParser.ParseImages(json));
        Assert.Equal(expected, item.ContainerCount);
    }

    [Fact]
    public void BadLine_ReportsLineNumberAndType()
    {
        var text = """
            {"ID":"ok1","Repository":"a","Tag":"b"}
            这不是 JSON
            """;
        var ex = Assert.Throws<FormatException>(() => WslcJsonParser.ParseImages(text));
        Assert.Contains("第 2 行", ex.Message);
        Assert.Contains("镜像", ex.Message);
    }

    [Fact]
    public void SameIdWithTwoTags_KeepsBothRows()
    {
        // 实测:wslc tag 之后 images 会为每个引用输出一行,ID 相同。
        var text = """
            {"ID":"aa","Repository":"busybox","Tag":"latest","Containers":"2"}
            {"ID":"aa","Repository":"my-busybox","Tag":"v1","Containers":"2"}
            """;
        var items = WslcJsonParser.ParseImages(text);
        Assert.Equal(2, items.Count);
        Assert.Equal("busybox:latest", items[0].Reference);
        Assert.Equal("my-busybox:v1", items[1].Reference);
    }
}