using WslcDesktop.Core.Models;
using WslcDesktop.Core.Parsing;

namespace WslcDesktop.Tests;

/// <summary>功能 003 · AC5/AC6:引用名与展示文本。</summary>
public class ImageInfoTests
{
    private static ImageInfo Parse(string json) => Assert.Single(WslcJsonParser.ParseImages(json));

    [Fact]
    public void TaggedImage_ReferenceIsRepositoryColonTag()
    {
        var image = Parse("""{"ID":"aaef90e06523","Repository":"docker.1ms.run/library/busybox","Tag":"latest"}""");

        Assert.True(image.IsTagged);
        Assert.Equal("docker.1ms.run/library/busybox:latest", image.Reference);
        Assert.Equal("docker.1ms.run/library/busybox:latest", image.DisplayName);
    }

    [Theory]
    [InlineData("<none>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    public void UntaggedImage_FallsBackToId(string repository)
    {
        var image = Parse($$"""{"ID":"deadbeef1234","Repository":"{{repository}}","Tag":"<none>"}""");

        Assert.False(image.IsTagged);
        Assert.Equal("deadbeef1234", image.Reference);          // AC5:无标签时用 ID 也能删
        Assert.Equal("无标签 · deadbeef1234", image.DisplayName);
    }

    [Fact]
    public void RepositoryWithoutTag_ReferenceIsRepositoryOnly()
    {
        var image = Parse("""{"ID":"i1","Repository":"myapp","Tag":"<none>"}""");

        Assert.Equal("myapp", image.Reference);
        Assert.Equal("myapp", image.DisplayName);
    }

    [Fact]
    public void ShortId_TruncatesToTwelve()
    {
        var image = Parse("""{"ID":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd"}""");
        Assert.Equal("0123456789ab", image.ShortId);
    }

    [Fact]
    public void CreatedText_ParsesWslcTimestampFormat()
    {
        // 真实形态:"2026-05-13 10:21:49 +0800 GMT+8"(AC6)
        var image = Parse("""{"ID":"a","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","CreatedSince":"5 months ago"}""");
        Assert.Equal("2026-05-13 10:21", image.CreatedText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("刚刚")]
    public void CreatedText_FallsBackToRelativeThenRaw(string createdAt)
    {
        var image = Parse($$"""{"ID":"a","CreatedAt":"{{createdAt}}","CreatedSince":"5 months ago"}""");
        Assert.Equal("5 months ago", image.CreatedText);
    }

    [Fact]
    public void CreatedText_UsesRawWhenNothingParses()
    {
        var image = Parse("""{"ID":"a","CreatedAt":"nonsense"}""");
        Assert.Equal("nonsense", image.CreatedText);
    }

    [Fact]
    public void UsageAndSizeText()
    {
        var inUse = Parse("""{"ID":"a","Repository":"r","Tag":"t","Size":"4.45MB","Containers":"2"}""");
        Assert.Equal("4.45MB", inUse.SizeText);
        Assert.Equal("被 2 个容器引用", inUse.UsageText);

        var free = Parse("""{"ID":"b","Repository":"r","Tag":"t","Size":"","Containers":"0"}""");
        Assert.Equal("—", free.SizeText);
        Assert.Equal("未被容器引用", free.UsageText);
    }

    [Fact]
    public void SubtleLine_ContainsIdAndCreated_OmitsPlaceholderDigest()
    {
        var image = Parse("""{"ID":"aaef90e06523","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"<none>"}""");
        Assert.Contains("ID aaef90e06523", image.SubtleLine);
        Assert.Contains("创建 2026-05-13 10:21", image.SubtleLine);
        Assert.DoesNotContain("摘要", image.SubtleLine);

        var withDigest = Parse("""{"ID":"aaef90e06523","CreatedAt":"2026-05-13 10:21:49 +0800 GMT+8","Digest":"sha256:fd7dc986"}""");
        Assert.Contains("摘要 sha256:fd7dc986", withDigest.SubtleLine);
    }

    [Fact]
    public void UntaggedFixture_ParsesAsTwoRows()
    {
        var items = WslcJsonParser.ParseImages(FixtureLoader.Read("wslc-images-untagged.ndjson"));

        Assert.Equal(2, items.Count);
        Assert.False(items[0].IsTagged);
        Assert.True(items[1].IsInUse);
    }
}