using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.Core.Services;

/// <summary>
/// 把"宣传贴网址"尽量解析成一条服务器条目（后台候选审核的「补全并编辑」用）。
///
/// 用户 2026-10-08 的要求：**点一下先自动填好字段，然后允许手动改**。
/// 所以这里只做"尽力而为"的通用抽取（论坛帖 / MC百科 / 博客都能沾点边），
/// 抽不到的字段留空，由后台表单补；**绝不因为抽不到就报错**。
///
/// 放在 Shared 里（纯逻辑、不联网）便于单测；联网抓取在服务端 `ThreadPrefill`。
/// </summary>
public static class ThreadPrefillParser
{
    /// <summary>明显不是服务器地址的域名（官网/论坛/网盘/百科等）。</summary>
    private static readonly string[] NonServerHosts =
    [
        "mcmod.cn", "mcbbs.co", "bilibili.com", "b23.tv", "qq.com", "weibo.com", "zhihu.com",
        "baidu.com", "pan.baidu.com", "lanzou", "123pan", "aliyundrive", "alipan", "quark",
        "github.com", "gitee.com", "youtube.com", "discord.gg", "curseforge.com", "modrinth.com",
        "minecraft.net", "mojang.com", "netease.com", "kuaishou.com", "douyin.com", "tieba.baidu.com",
    ];

    /// <summary>从已经抓下来的 HTML 里抽取字段（不联网，便于单测）。</summary>
    public static (ServerEntry Entry, string Note) Parse(string html, string threadUrl)
    {
        var notes = new List<string>();
        var text = HtmlToText(html);

        var name = ExtractTitle(html);
        if (string.IsNullOrWhiteSpace(name))
        {
            notes.Add("没取到标题，请手动填写名称");
        }

        var (host, port) = ExtractAddress(text);
        if (host is null)
        {
            notes.Add("没识别到直连地址，请手动填写（也可能是没有公开地址的服务器）");
        }

        var version = ExtractVersion(text);
        var login = ExtractLoginMode(text);
        var premium = ExtractPremium(text);
        var whitelist = ExtractYesNo(text, "白名单");
        var (loader, categories) = ExtractLoaderAndCategories(text);
        var qq = ExtractQqGroup(text);
        var site = ExtractOfficialSite(html, threadUrl);
        var description = ExtractDescription(html, text, name);

        var entry = new ServerEntry
        {
            Id = BuildId(name, host, threadUrl),
            Name = name ?? string.Empty,
            Address = host ?? string.Empty,
            Port = port,
            Version = version ?? string.Empty,
            VersionGroups = VersionGroupsOf(version),
            Loader = loader,
            Region = ServerCategories.RegionDomestic,
            Categories = categories,
            Description = description,
            LongDescription = description,
            RequirePremium = premium,
            Whitelist = whitelist,
            LoginMode = login,
            QqGroup = qq,
            OfficialSite = site,
            SourceUrl = threadUrl,
            SourceName = "宣传贴（后台补全）",
        };

        return (entry, notes.Count == 0 ? "已按宣传贴内容自动填好，请核对后再保存。" : string.Join("；", notes));
    }

    // ---------- 解析 ----------

    private static string? ExtractTitle(string html)
    {
        var title = FirstGroup(html, @"<title[^>]*>([\s\S]*?)</title>");
        title ??= FirstGroup(html, @"<meta[^>]+property=[""']og:title[""'][^>]+content=[""']([^""']+)");
        title ??= FirstGroup(html, @"<h1[^>]*>([\s\S]*?)</h1>");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var clean = HtmlToText(title!);
        // 去掉常见站点后缀："xxx - MCBBS" / "xxx_我的世界百科" / "xxx | MC百科"
        clean = Regex.Split(clean, @"\s*[-|_—–]\s*").FirstOrDefault() ?? clean;
        // 注意：这里**不**再剥"服务器/介绍"这类词——"方块服务器"会被剥成"方块"。
        // 名称填得不够准没关系，后台表单本来就要人工核对。
        clean = clean.Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>地址：优先"IP/服务器地址"关键词附近；其次任意 IPv4 / play.xxx 域名。</summary>
    private static (string? Host, int Port) ExtractAddress(string text)
    {
        var candidates = new List<string>();

        foreach (Match match in Regex.Matches(
                     text,
                     @"(?:服务器)?(?:IP|ip|地址|直连|连接地址|加入地址)\s*[:：=]?\s*([A-Za-z0-9][A-Za-z0-9\.\-]{2,60}(?::\d{1,5})?)",
                     RegexOptions.IgnoreCase))
        {
            candidates.Add(match.Groups[1].Value);
        }

        foreach (Match match in Regex.Matches(text, @"\b((?:\d{1,3}\.){3}\d{1,3})(?::(\d{1,5}))?"))
        {
            candidates.Add(match.Value);
        }

        foreach (Match match in Regex.Matches(text, @"\b((?:play|mc|srv|game|server)[A-Za-z0-9\-]*\.[A-Za-z0-9\-\.]+\.[A-Za-z]{2,})(?::(\d{1,5}))?", RegexOptions.IgnoreCase))
        {
            candidates.Add(match.Value);
        }

        foreach (var raw in candidates)
        {
            var value = raw.Trim().TrimEnd('.', ',', ';', '。', '，');
            var host = value;
            var port = 25565;
            var colon = value.LastIndexOf(':');
            if (colon > 0 && int.TryParse(value[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
            {
                host = value[..colon];
                port = parsed;
            }

            if (host.Length < 4 || host.Contains('@') || NonServerHosts.Any(h => host.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // 反查：能解析成域名/IP 形状才要
            if (!Regex.IsMatch(host, @"^((\d{1,3}\.){3}\d{1,3}|([A-Za-z0-9\-]+\.)+[A-Za-z]{2,})$"))
            {
                continue;
            }

            return (host, port);
        }

        return (null, 25565);
    }

    private static string? ExtractVersion(string text)
    {
        var match = Regex.Match(text, @"(?:服务端版本号|游戏版本|支持版本|版本)\s*[:：]?\s*([0-9]{1,2}(?:\.[0-9]{1,2}){1,2}(?:\.x)?)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        // 兜底：只认带点的版本号（"22" 这种孤立数字不算，避免把 revision/人数当成版本）
        match = Regex.Match(text, @"\b(1\.[0-9]{1,2}(?:\.[0-9]{1,2})?|2[0-9]\.[0-9]{1,2})\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyList<string> VersionGroupsOf(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return [];
        }

        var key = version!;
        if (!key.Contains(".x", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split('.');
            key = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}.x" : key;
        }

        return ServerCategories.VersionGroups.Contains(key) ? [key] : [];
    }

    private static (string Loader, IReadOnlyList<string> Categories) ExtractLoaderAndCategories(string text)
    {
        var categories = new List<string>();
        var loader = ServerCategories.LoaderVanilla;

        if (Regex.IsMatch(text, @"neoforge", RegexOptions.IgnoreCase))
        {
            loader = ServerCategories.LoaderNeoForge;
        }
        else if (Regex.IsMatch(text, @"fabric", RegexOptions.IgnoreCase))
        {
            loader = ServerCategories.LoaderFabric;
        }
        else if (Regex.IsMatch(text, @"forge", RegexOptions.IgnoreCase))
        {
            loader = ServerCategories.LoaderForge;
        }
        else if (Regex.IsMatch(text, @"(插件|spigot|paper|bukkit)", RegexOptions.IgnoreCase))
        {
            loader = ServerCategories.LoaderVanilla;
            categories.Add(ServerCategories.PluginServer);
        }

        if (loader is not ServerCategories.LoaderVanilla)
        {
            categories.Add(ServerCategories.Modded);
        }

        foreach (var (keyword, category) in new[]
                 {
                     ("生存", ServerCategories.Survival), ("创造", ServerCategories.Creative),
                     ("小游戏", ServerCategories.MiniGame), ("rpg", ServerCategories.Rpg),
                     ("建筑", ServerCategories.Building), ("空岛", ServerCategories.SkyBlock),
                     ("监狱", ServerCategories.Prison), ("pvp", ServerCategories.Pvp),
                     ("无政府", ServerCategories.Anarchy), ("宝可梦", ServerCategories.Pokemon),
                     ("角色扮演", ServerCategories.Roleplay),
                 })
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase) && !categories.Contains(category))
            {
                categories.Add(category);
            }
        }

        if (categories.Count == 0)
        {
            categories.Add(ServerCategories.Survival);
        }

        return (loader, categories);
    }

    private static string? ExtractLoginMode(string text)
    {
        if (text.Contains("统一通行证", StringComparison.Ordinal))
        {
            return "统一通行证";
        }

        if (Regex.IsMatch(text, @"(皮肤站|外置登录|littleskin|yggdrasil)", RegexOptions.IgnoreCase))
        {
            return "外置登录（皮肤站）";
        }

        return null;
    }

    private static bool? ExtractPremium(string text) =>
        ExtractYesNo(text, "正版验证") ?? ExtractYesNo(text, "正版");

    private static bool? ExtractYesNo(string text, string label)
    {
        var match = Regex.Match(text, Regex.Escape(label) + @"(?:验证)?\s*[:：]?\s*(有|无|是|否|需要|不需要|开启|关闭)");
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value is "有" or "是" or "需要" or "开启";
    }

    private static string? ExtractQqGroup(string text)
    {
        var match = Regex.Match(text, @"(?:qq|交流|玩家)?群\D{0,6}(\d{6,11})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractOfficialSite(string html, string threadUrl)
    {
        var host = SafeHost(threadUrl);
        foreach (Match match in Regex.Matches(html, @"href=[""'](https?://[^""']+)[""']", RegexOptions.IgnoreCase))
        {
            var url = match.Groups[1].Value;
            var target = SafeHost(url);
            if (target.Length == 0 || target == host || NonServerHosts.Any(h => target.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return url;
        }

        return null;
    }

    private static string ExtractDescription(string html, string text, string? name)
    {
        var meta = FirstGroup(html, @"<meta[^>]+name=[""']description[""'][^>]+content=[""']([^""']+)");
        meta ??= FirstGroup(html, @"<meta[^>]+property=[""']og:description[""'][^>]+content=[""']([^""']+)");
        var description = HtmlToText(meta ?? string.Empty).Trim();

        if (description.Length == 0)
        {
            // 退而求其次：正文里去掉标题后的第一段有内容的文字
            description = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length >= 15 && (name is null || !line.Contains(name, StringComparison.Ordinal)))
                ?? string.Empty;
        }

        description = Regex.Replace(description, @"\s+", " ").Trim();
        return description.Length <= 120 ? description : description[..120] + "…";
    }

    private static string BuildId(string? name, string? host, string threadUrl)
    {
        var ascii = new string((name ?? string.Empty)
            .Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? char.ToLowerInvariant(c)
                : c is ' ' or '-' or '_' ? '-' : '\0')
            .Where(c => c != '\0')
            .ToArray())
            .Trim('-');
        ascii = Regex.Replace(ascii, "-{2,}", "-");

        if (ascii.Length >= 3)
        {
            return ascii[..Math.Min(40, ascii.Length)];
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            return Regex.Replace(host!, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        }

        return "thread-" + Math.Abs(threadUrl.GetHashCode()).ToString("x");
    }

    // ---------- HTML 工具 ----------

    /// <summary>
    /// 只抽**正文介绍部分**的图片（用户 2026-10-10 要求）。
    ///
    /// 做法：先按常见论坛/百科/博客的正文容器定位（Discuz 的 <c>postmessage_*</c> / <c>t_f</c>、
    /// <c>&lt;article&gt;</c>、以及若干常见的正文 class/id）；**定位不到就一个都不抓**，
    /// 并把 <c>Uncertain</c> 置真——界面据此提示"可能有图但没抓"。
    /// 返回的 URL 都换算成绝对地址，去重后最多 8 张。
    /// </summary>
    public static (IReadOnlyList<string> Urls, bool Uncertain) ExtractBodyImages(string html, string pageUrl)
    {
        var region = FindBodyRegion(html);
        if (region is null)
        {
            return (Array.Empty<string>(), true);
        }

        var urls = new List<string>();
        foreach (Match match in Regex.Matches(region, @"<img\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = match.Value;
            var raw = FirstGroup(tag, @"(?:data-original|data-src|data-lazy-src|data-echo|data-url|src)\s*=\s*[""']([^""']+)[""']");
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var absolute = ResolveUrl(pageUrl, raw!);
            if (absolute is null || !LooksLikeContentImage(tag, absolute))
            {
                continue;
            }

            if (!urls.Contains(absolute, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(absolute);
            }

            if (urls.Count >= 8)
            {
                break;
            }
        }

        return (urls, false);
    }

    /// <summary>正文容器的**内层 HTML**；定位不到返回 null（= 不确定，别抓）。</summary>
    private static string? FindBodyRegion(string html)
    {
        // ① Discuz 系（MCBBS）：正文单元格
        foreach (var pattern in new[]
                 {
                     @"<(t[dh])\b[^>]*\bid=[""']postmessage_\d+[""'][^>]*>",
                     @"<(t[dh])\b[^>]*\bclass=[""'][^""']*\bt_f\b[^""']*[""'][^>]*>",
                 })
        {
            var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var inner = InnerOf(html, match.Index + match.Length, match.Groups[1].Value.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return inner;
            }
        }

        // ② 语义标签
        var article = Regex.Match(html, @"<article\b[^>]*>", RegexOptions.IgnoreCase);
        if (article.Success)
        {
            var inner = InnerOf(html, article.Index + article.Length, "article");
            if (!string.IsNullOrWhiteSpace(inner) && inner!.Length > 120)
            {
                return inner;
            }
        }

        // ③ 常见正文容器（div）
        var div = Regex.Match(html,
            @"<div\b[^>]*(?:id|class)\s*=\s*[""'][^""']*(?:article-?content|article_content|common-text|post-?content|entry-content|content-body|markdown-body|rich_media_content|js_content|thread-content)[^""']*[""'][^>]*>",
            RegexOptions.IgnoreCase);
        if (div.Success)
        {
            var inner = InnerOf(html, div.Index + div.Length, "div");
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return inner;
            }
        }

        return null;
    }

    /// <summary>从 <paramref name="start"/> 开始，按同名标签配对，取到闭合标签之前的 HTML。</summary>
    private static string? InnerOf(string html, int start, string tag)
    {
        if (start < 0 || start >= html.Length)
        {
            return null;
        }

        var open = new Regex(@"<" + tag + @"\b", RegexOptions.IgnoreCase);
        var close = new Regex(@"</" + tag + @"\s*>", RegexOptions.IgnoreCase);
        var depth = 1;
        var cursor = start;
        while (cursor < html.Length)
        {
            var nextOpen = open.Match(html, cursor);
            var nextClose = close.Match(html, cursor);
            if (!nextClose.Success)
            {
                break;
            }

            if (nextOpen.Success && nextOpen.Index < nextClose.Index)
            {
                depth++;
                cursor = nextOpen.Index + nextOpen.Length;
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return html.Substring(start, nextClose.Index - start);
            }

            cursor = nextClose.Index + nextClose.Length;
        }

        return null;
    }

    private static string? ResolveUrl(string baseUrl, string raw)
    {
        var value = raw.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return new Uri(new Uri(baseUrl), value).ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>排除表情/头像/图标这类"不是介绍图"的图。</summary>
    private static bool LooksLikeContentImage(string tag, string url)
    {
        var lower = url.ToLowerInvariant();
        foreach (var bad in new[]
                 {
                     "smiley", "/emot", "emotion", "avatar", "uc_server", "static/image/common",
                     "/icon/", "gravatar", "captcha", "logo.png", "loading.gif", "spacer.gif",
                 })
        {
            if (lower.Contains(bad, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // 明确写了很小的宽高（表情/图标）就跳过
        var size = FirstGroup(tag, @"\b(?:width|height)\s*=\s*[""']?(\d{1,3})[""']?");
        if (size is not null && int.TryParse(size, out var px) && px > 0 && px < 60)
        {
            return false;
        }

        return true;
    }

    private static string? FirstGroup(string html, string pattern)
    {
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string SafeHost(string url)
    {
        try
        {
            return new Uri(url).Host.ToLowerInvariant();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>粗略去标签：脚本/样式整段丢掉，块级标签换成换行，再去实体。</summary>
    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", string.Empty, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</p>|</div>|</li>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t\u00A0]+", " ").Trim();
    }
}
