using McServerList.Core.Models;

namespace McServerList.Core.Tests;

public class ServerTextFormatterTests
{
    [Theory]
    [InlineData("§c维护 §l服务器", "维护 服务器")]
    [InlineData("&aGreen &lBold&r", "Green Bold")]
    [InlineData("§#FF0000红色文字", "红色文字")]
    [InlineData("§x§F§F§0§0§0§0六位色", "六位色")]
    [InlineData("正常文字", "正常文字")]
    public void 颜色代码被删除(string input, string expected) =>
        Assert.Equal(expected, ServerTextFormatter.StripColorCodes(input));

    [Theory]
    [InlineData("AT&T 官方", "AT&T 官方")]
    [InlineData("P&G", "P&G")]
    public void 普通与号不会被误删(string input, string expected) =>
        Assert.Equal(expected, ServerTextFormatter.StripColorCodes(input));

    [Fact]
    public void 长文本会被断行()
    {
        var text = string.Concat(Enumerable.Repeat("这是一段很长的服务器介绍文字，用于验证自动断行是否生效。", 6));
        var formatted = ServerTextFormatter.ToParagraphs(text, maxLineLength: 60);

        Assert.Contains("\n", formatted);
        Assert.True(formatted.Split('\n').Length > 1);
    }

    [Fact]
    public void 条目符号前会换行()
    {
        var formatted = ServerTextFormatter.ToParagraphs("服务器介绍：这里是正文。① 第一个特色。② 第二个特色。");

        Assert.Contains("\n①", formatted);
    }

    [Fact]
    public void 空文本返回空串()
    {
        Assert.Equal(string.Empty, ServerTextFormatter.StripColorCodes(null));
        Assert.Equal(string.Empty, ServerTextFormatter.ToParagraphs("   "));
    }
}
