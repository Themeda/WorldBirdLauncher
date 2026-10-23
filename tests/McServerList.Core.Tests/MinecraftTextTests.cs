using System.Text.Json;
using McServerList.Core.Protocol;

namespace McServerList.Core.Tests;

public class MinecraftTextTests
{
    [Fact]
    public void Flatten_支持字符串对象与extra数组()
    {
        using var document = JsonDocument.Parse("""
        { "text": "A", "extra": [ "B", { "text": "C" } ] }
        """);

        Assert.Equal("ABC", MinecraftText.Flatten(document.RootElement));
    }

    [Fact]
    public void Flatten_支持纯字符串与数组()
    {
        using var plain = JsonDocument.Parse("\"hello\"");
        Assert.Equal("hello", MinecraftText.Flatten(plain.RootElement));

        using var array = JsonDocument.Parse("""["a", {"text":"b"}]""");
        Assert.Equal("ab", MinecraftText.Flatten(array.RootElement));
    }

    [Fact]
    public void Normalize_去除颜色代码并压缩空白()
    {
        Assert.Equal("欢迎来到 测试服", MinecraftText.Normalize("§a欢迎§r来到 §l测试服"));
        Assert.Equal("A B", MinecraftText.Normalize("A\n  B"));
    }

    // ---------- 2026-10-08 付费模块：MOTD 颜色/换行解析 ----------

    [Fact]
    public void FlattenFormatted_把JSON文本组件的颜色与格式还原成颜色代码()
    {
        using var document = JsonDocument.Parse("""{ "text": "A", "color": "red", "bold": true }""");
        Assert.Equal("§c§lA", MinecraftText.FlattenFormatted(document.RootElement));
    }

    [Fact]
    public void FlattenFormatted_兄弟节点各自从父节点继承样式()
    {
        using var document = JsonDocument.Parse("""
        { "text": "A", "color": "red", "extra": [ { "text": "B", "color": "blue" }, "C" ] }
        """);

        // A 红 → B 蓝 → C 回到父节点的红
        Assert.Equal("§cA§9B§cC", MinecraftText.FlattenFormatted(document.RootElement));
    }

    [Fact]
    public void FlattenFormatted_没有颜色时不加代码()
    {
        using var document = JsonDocument.Parse("""{ "text": "普通", "extra": [" 文本"] }""");
        Assert.Equal("普通 文本", MinecraftText.FlattenFormatted(document.RootElement));
    }

    [Fact]
    public void ParseSegments_解析颜色格式与换行()
    {
        var segments = MinecraftText.ParseSegments("§a绿§l粗\n§r普通");

        Assert.Equal(3, segments.Count);
        Assert.Equal("绿", segments[0].Text);
        Assert.Equal("#55FF55", segments[0].Color);
        Assert.False(segments[0].Bold);

        // 换行留在"样式切换之前"的那一段里（渲染时照样换行）
        Assert.Equal("粗\n", segments[1].Text);
        Assert.Equal("#55FF55", segments[1].Color);
        Assert.True(segments[1].Bold);

        Assert.Equal("普通", segments[2].Text);
        Assert.Null(segments[2].Color);
        Assert.False(segments[2].Bold);
    }

    [Fact]
    public void ParseSegments_支持井号十六进制与x风格十六进制()
    {
        var hex = MinecraftText.ParseSegments("§#FF00AA甲");
        Assert.Equal("#FF00AA", hex[0].Color);
        Assert.Equal("甲", hex[0].Text);

        var xStyle = MinecraftText.ParseSegments("§x§1§2§3§4§5§6乙");
        Assert.Equal("#123456", xStyle[0].Color);
        Assert.Equal("乙", xStyle[0].Text);
    }

    [Fact]
    public void KeepFormatting_只去掉乱码代码并保留颜色与换行()
    {
        Assert.Equal("§a第一行\n§b第二行", MinecraftText.KeepFormatting("§a§k第一行\n§b第二行§k"));
    }
}
