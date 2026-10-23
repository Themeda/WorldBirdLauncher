using WorldBirdLauncher.Core.Models;
using WorldBirdLauncher.Core.Services;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 宣传贴 → 服务器条目的自动补全（2026-10-08 用户要求"点一下先自动填写字段"）。
/// 只测纯逻辑的解析部分（抓取在服务端），样例是典型的中文论坛宣传贴。
/// </summary>
public class ThreadPrefillParserTests
{
    private const string ForumPost = """
        <html><head>
        <title>邦溪纯净 - 我的世界服务器 - MCBBS</title>
        <meta name="description" content="大型综合服务器，包含空岛、生存、起床战争等多种玩法。">
        </head><body>
        <div class="post">
          <p>服务器名称：邦溪纯净</p>
          <p>服务器IP：mc.bangxi.top</p>
          <p>服务端版本号：1.20.1</p>
          <p>正版验证：无</p>
          <p>白名单：无</p>
          <p>QQ群：123456789</p>
          <p>支持 Forge 模组，玩法有生存、空岛、RPG</p>
          <a href="https://mc.bangxi.top">官网</a>
          <a href="https://www.mcbbs.co/thread-1-1.html">原帖</a>
          <a href="https://pan.baidu.com/s/xxx">客户端下载</a>
        </div>
        </body></html>
        """;

    [Fact]
    public void 论坛宣传贴能抽出主要字段()
    {
        var (entry, note) = ThreadPrefillParser.Parse(ForumPost, "https://www.mcbbs.co/thread-1-1.html");

        Assert.Equal("邦溪纯净", entry.Name);
        Assert.Equal("mc.bangxi.top", entry.Address);
        Assert.Equal(25565, entry.Port);
        Assert.Equal("1.20.1", entry.Version);
        Assert.Equal(["1.20.x"], entry.VersionGroups);
        Assert.Equal(ServerCategories.LoaderForge, entry.Loader);
        Assert.Contains(ServerCategories.Modded, entry.Categories);
        Assert.Contains(ServerCategories.Survival, entry.Categories);
        Assert.Contains(ServerCategories.SkyBlock, entry.Categories);
        Assert.False(entry.RequirePremium);
        Assert.False(entry.Whitelist);
        Assert.Equal("123456789", entry.QqGroup);
        Assert.Equal("https://mc.bangxi.top", entry.OfficialSite);   // 跳过论坛/网盘链接
        Assert.Contains("空岛", entry.Description);
        Assert.Equal("https://www.mcbbs.co/thread-1-1.html", entry.SourceUrl);
        Assert.False(string.IsNullOrWhiteSpace(entry.Id));
        Assert.DoesNotContain("没识别到直连地址", note);
    }

    [Fact]
    public void 带端口与正版验证的帖子()
    {
        const string html = """
            <html><head><title>PikaNetwork | 服务器列表</title></head><body>
            <p>地址：play.pika-network.net:25566</p>
            <p>版本：1.20.x</p>
            <p>正版验证：需要</p>
            <p>白名单：需要</p>
            </body></html>
            """;

        var (entry, _) = ThreadPrefillParser.Parse(html, "https://example.com/t/1");

        Assert.Equal("PikaNetwork", entry.Name);
        Assert.Equal("play.pika-network.net", entry.Address);
        Assert.Equal(25566, entry.Port);
        Assert.Equal("1.20.x", entry.Version);
        Assert.True(entry.RequirePremium);
        Assert.True(entry.Whitelist);
        Assert.Equal(ServerCategories.LoaderVanilla, entry.Loader);
    }

    [Fact]
    public void 抽不到字段时不报错而是给出提示()
    {
        const string html = "<html><head><title>某服务器介绍</title></head><body><p>这里只有一段介绍文字。</p></body></html>";

        var (entry, note) = ThreadPrefillParser.Parse(html, "https://example.com/thread/9");

        Assert.Equal("某服务器介绍", entry.Name);        // 名称原样保留，交给后台人工核对
        Assert.Equal(string.Empty, entry.Address);
        Assert.Contains("没识别到直连地址", note);
        Assert.False(string.IsNullOrWhiteSpace(entry.Id)); // 至少给个能用的 id
    }

    // ---------- 正文图片（用户 2026-10-10：只抓正文介绍部分的图） ----------

    [Fact]
    public void 只抓正文单元格里的图片_并且转成绝对地址()
    {
        const string html = """
            <html><body>
            <div class="sidebar"><img src="/static/image/common/logo.png" /></div>
            <table><tr><td class="t_f" id="postmessage_1">
              <p>服务器介绍：</p>
              <img src="data/attach/1.png" />
              <img data-original="https://cdn.example.com/shot2.jpg" src="loading.gif" />
              <img src="/static/image/smiley/1.gif" />
            </td></tr></table>
            </body></html>
            """;

        var (urls, uncertain) = ThreadPrefillParser.ExtractBodyImages(html, "https://bbs.example.com/thread-1-1.html");

        Assert.False(uncertain);
        Assert.Equal(["https://bbs.example.com/data/attach/1.png", "https://cdn.example.com/shot2.jpg"], urls);
    }

    [Fact]
    public void article正文里的图片也能抓到()
    {
        const string html = """
            <html><body>
            <article><h1>某个服务器</h1><p>介绍文字介绍文字介绍文字介绍文字介绍文字介绍文字</p>
              <p>玩法：生存、空岛、起床战争；支持 Forge 模组；群号 123456789；官网 https://example.com</p>
              <img src="pic/a.png" width="800" height="450" /></article>
            <div class="footer"><img src="footer.png" /></div>
            </body></html>
            """;

        var (urls, uncertain) = ThreadPrefillParser.ExtractBodyImages(html, "https://blog.example.com/p/1");

        Assert.False(uncertain);
        Assert.Equal(["https://blog.example.com/p/pic/a.png"], urls);
    }

    [Fact]
    public void 定位不到正文时不抓图并标记不确定()
    {
        const string html = "<html><body><div class=\"somewhere\"><img src=\"a.png\" /></div></body></html>";

        var (urls, uncertain) = ThreadPrefillParser.ExtractBodyImages(html, "https://example.com/x");

        Assert.True(uncertain);
        Assert.Empty(urls);
    }
}
