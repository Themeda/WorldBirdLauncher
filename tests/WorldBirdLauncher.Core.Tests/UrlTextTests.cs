using WorldBirdLauncher.Core.Text;

namespace WorldBirdLauncher.Core.Tests;

public class UrlTextTests
{
    [Theory]
    // 网址后面粘着正文（论坛正文换行被合并后常见的形态）
    [InlineData("服务器官网：https://mckkk.comQQ 群号：535525771", "https://mckkk.com", "QQ 群号：535525771")]
    [InlineData("https://map.spruce.town/查阅自己的建筑所在地", "https://map.spruce.town/", "查阅自己的建筑所在地")]
    [InlineData("https://littleskin.cn/user注册账号以后直接登录本服务器，支持双重验证。", "https://littleskin.cn/user", "注册账号以后直接登录本服务器，支持双重验证。")]
    [InlineData("官网 http://www.java.com,或者进入QQ群", "http://www.java.com", ",或者进入QQ群")]
    [InlineData("https://www.mc1.top/。电脑端使用", "https://www.mc1.top/", "。电脑端使用")]
    // 正常网址不能被截断
    [InlineData("https://play.mcmod.cn/sv20188852.html", "https://play.mcmod.cn/sv20188852.html", "")]
    [InlineData("http://sc.gygkhd.fun:9001/", "http://sc.gygkhd.fun:9001/", "")]
    [InlineData("https://example.community/path", "https://example.community/path", "")]
    [InlineData("https://www.example.com/a?b=1#c", "https://www.example.com/a?b=1#c", "")]
    // 中文域名要保留
    [InlineData("官网：https://中文.中国/服务器列表", "https://中文.中国/服务器列表", "")]
    [InlineData("https://我的服务器.公司", "https://我的服务器.公司", "")]
    [InlineData("https://中文域名.中国介绍", "https://中文域名.中国", "介绍")]
    public void 能从粘连文本里切出正确网址(string text, string expectedUrl, string expectedRest)
    {
        var match = Assert.Single(UrlText.Find(text));
        Assert.Equal(expectedUrl, match.Url);
        // 匹配从网址起点开始；Rest + 匹配之后剩下的原文 = 被吞掉的那部分文字
        Assert.Equal(text.IndexOf("http", StringComparison.OrdinalIgnoreCase), match.Start);
        Assert.Equal(expectedRest, match.Rest + text[(match.Start + match.Length)..]);
    }

    [Fact]
    public void 分离文本会把粘连的网址和正文拆开()
    {
        var cleaned = UrlText.Separate("服务器官网：https://mckkk.comQQ 群号：535525771，欢迎加入");

        Assert.Equal("服务器官网：https://mckkk.com QQ 群号：535525771，欢迎加入", cleaned);
    }

    [Fact]
    public void 分离文本时保留前后原文()
    {
        var cleaned = UrlText.Separate("见 https://map.spruce.town/查阅自己的建筑所在地 或加群");

        Assert.Equal("见 https://map.spruce.town/ 查阅自己的建筑所在地 或加群", cleaned);
    }

    [Fact]
    public void 没有网址时原样返回()
    {
        Assert.Equal("没有网址的文本", UrlText.Separate("没有网址的文本"));
        Assert.Empty(UrlText.Find("没有网址的文本"));
    }
}
