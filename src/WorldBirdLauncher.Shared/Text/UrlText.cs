using System.Text;
using System.Text.RegularExpressions;

namespace WorldBirdLauncher.Core.Text;

/// <summary>文本里找到的一处网址：<see cref="Url"/> 是清洗后的网址，<see cref="Rest"/> 是被粘连进来的多余文字。</summary>
public readonly record struct UrlMatch(int Start, int Length, string Url, string Rest);

/// <summary>
/// 文本里的网址识别与清洗。
/// 来源帖子（论坛/MC百科）常把网址和后面的文字粘在一起，例如
/// <c>服务器官网：https://mckkk.comQQ 群号：535525771</c>、
/// <c>https://map.spruce.town/查阅自己的建筑所在地</c>，
/// 直接按「非空白」匹配会把 <c>QQ</c> 或整段中文吞进链接里。
/// 这里用「域名后缀收敛 + 中文判断」把真正的网址切出来，中文域名（如 <c>中文.中国</c>）照常保留。
/// </summary>
public static class UrlText
{
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s<>""'’“”]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>常见顶级域名（含中文顶级域名）。收敛主机名时用。</summary>
    private static readonly string[] KnownTlds =
    [
        // 通用 / 国别
        "com", "cn", "net", "org", "xyz", "top", "cc", "me", "io", "info", "tv", "biz", "pro", "edu", "gov",
        "fun", "moe", "club", "online", "site", "space", "world", "life", "live", "dev", "app", "icu", "best",
        "art", "cool", "blog", "one", "run", "red", "link", "ltd", "group", "team", "games", "gg", "im", "eu",
        "us", "uk", "jp", "kr", "ru", "de", "fr", "ca", "au", "pl", "nl", "se", "no", "fi", "dk", "it", "es",
        "ch", "at", "be", "cz", "gr", "hu", "ie", "il", "in", "ir", "is", "lt", "lu", "lv", "mx", "my", "nz",
        "pt", "ro", "rs", "sg", "sk", "th", "tr", "tw", "ua", "vn", "za", "hk", "mo", "wiki", "cloud", "host",
        "tech", "design", "studio", "media", "agency", "solutions", "services", "works", "tools", "website",
        "shop", "store", "pub", "cafe", "network", "digital", "email", "finance", "fund", "care", "fans",
        // 中文顶级域名
        "中国", "公司", "网络", "我爱你", "商城", "网店", "网址", "集团", "在线", "中文网", "移动", "信息",
        "广东", "上海", "北京", "游戏", "企业", "商店", "网站",
    ];

    /// <summary>中文标点：网址后紧跟这些符号时一定已经结束了（不含 ASCII 标点，避免误伤 URL 自身的 : ? , 等）。</summary>
    private static readonly char[] CjkPunctuation = ['。', '，', '、', '；', '：', '！', '？', '（', '）', '【', '】', '《', '》', '「', '」', '『', '』', '…', '·', '　'];

    /// <summary>找出文本里的全部网址（已清洗）。</summary>
    public static IReadOnlyList<UrlMatch> Find(string? text)
    {
        var matches = new List<UrlMatch>();
        if (string.IsNullOrEmpty(text))
        {
            return matches;
        }

        foreach (Match match in UrlPattern.Matches(text))
        {
            var (url, rest) = Split(match.Value);
            if (url.Length == 0)
            {
                continue;
            }

            matches.Add(new UrlMatch(match.Index, match.Length, url, rest));
        }

        return matches;
    }

    /// <summary>
    /// 把一段文本里所有「网址和后面的字粘在一起」的地方拆开（网址与剩余文字之间补一个空格）。
    /// 返回的文本可直接写回数据文件。
    /// </summary>
    public static string Separate(string? text)
    {
        var matches = Find(text);
        if (matches.Count == 0)
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder();
        var position = 0;
        foreach (var match in matches)
        {
            builder.Append(text!, position, match.Start - position);
            builder.Append(match.Url);
            if (match.Rest.Length > 0)
            {
                builder.Append(' ').Append(match.Rest);
            }

            position = match.Start + match.Length;
        }

        builder.Append(text!, position, text!.Length - position);
        return builder.ToString();
    }

    /// <summary>把贪心匹配到的一段切分成「网址」和「被粘连的多余文字」。</summary>
    public static (string url, string rest) Split(string raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        // 1) 遇到中文标点就截断（后续内容一定是正文，原样保留在 Rest 里）
        var dropped = string.Empty;
        var punctuation = value.IndexOfAny(CjkPunctuation);
        if (punctuation >= 0)
        {
            dropped = value[punctuation..];
            value = value[..punctuation];
        }

        // 2) 先摘掉协议头，再拆成「主机部分 / 路径部分」
        var schemeEnd = value.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd < 0 ? string.Empty : value[..(schemeEnd + 3)];
        var afterScheme = schemeEnd < 0 ? value : value[(schemeEnd + 3)..];
        var tailStart = afterScheme.IndexOfAny(['/', '?', '#']);
        var authority = tailStart < 0 ? afterScheme : afterScheme[..tailStart];
        var tail = tailStart < 0 ? string.Empty : afterScheme[tailStart..];

        authority = TrimAuthority(authority, out var restOfAuthority);
        var rest = restOfAuthority;
        if (restOfAuthority.Length > 0)
        {
            // 主机名后面就粘连了文字，后面的"路径"其实也是正文
            rest += tail;
            tail = string.Empty;
        }
        else if (FirstCjkRun(authority, minimumLength: 1) >= 0)
        {
            // 中文域名（中文.中国）的路径不做中文截断——这类站点的路径本身可能就是中文
        }
        else
        {
            tail = TrimCjk(tail, out var restOfTail);
            rest += restOfTail;
        }

        var url = scheme + authority + tail;
        var trimmed = url.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"', '）', ')', ']', '}', '。', '，', '、', '；', '：', '！', '？');
        if (trimmed.Length < url.Length)
        {
            rest = url[trimmed.Length..] + rest;
            url = trimmed;
        }

        return (url, rest + dropped);
    }

    /// <summary>
    /// 清洗主机名：去掉多余的用户名/密码，并把「被粘连的字母」切掉。
    /// 例：<c>mckkk.comQQ</c> → 网址 <c>mckkk.com</c>、剩余 <c>QQ</c>。
    /// </summary>
    private static string TrimAuthority(string authority, out string rest)
    {
        rest = string.Empty;
        if (authority.Length == 0)
        {
            return authority;
        }

        // 去掉 user:pass@
        var at = authority.LastIndexOf('@');
        var userInfo = at >= 0 ? authority[..(at + 1)] : string.Empty;
        var host = at >= 0 ? authority[(at + 1)..] : authority;

        // 端口单独处理：主机名在做后缀收敛时不能带端口
        var port = string.Empty;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.Length > colon + 1 && host[(colon + 1)..].All(NetCompat.IsAsciiDigit))
        {
            port = host[colon..];
            host = host[..colon];
        }

        if (!EndsWithKnownTld(host))
        {
            // 中文域名（中文.中国）在上面的 TLD 判断里已放行；这里剩下的是"中文正文粘在主机名后面"
            var cjkStart = FirstCjkRun(host, minimumLength: 2);
            if (cjkStart > 0)
            {
                rest = host[cjkStart..] + port;
                host = host[..cjkStart];
            }
            else
            {
                // 后缀收敛：example.cn/... 保留；mckkk.comQQ → 切到 mckkk.com
                var cut = FindTldCut(host);
                if (cut > 0 && cut < host.Length)
                {
                    rest = host[cut..] + port;
                    host = host[..cut];
                }
            }
        }

        return userInfo + host.TrimEnd('.') + (rest.Length == 0 ? port : string.Empty);
    }

    /// <summary>路径里的中文正文（连续 2 字以上）直接从那里截断。</summary>
    private static string TrimCjk(string tail, out string rest)
    {
        rest = string.Empty;
        if (tail.Length == 0)
        {
            return tail;
        }

        var start = FirstCjkRun(tail, minimumLength: 2);
        if (start < 0)
        {
            return tail;
        }

        rest = tail[start..];
        return tail[..start];
    }

    private static bool EndsWithKnownTld(string host)
    {
        var trimmed = host.TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var dot = trimmed.LastIndexOf('.');
        var label = dot >= 0 ? trimmed[(dot + 1)..] : trimmed;
        return IsKnownTld(label);
    }

    /// <summary>找到「主机名应该切到哪里」：例如 comQQ 里的 QQ 要切掉。</summary>
    private static int FindTldCut(string host)
    {
        var best = -1;
        for (var dot = host.LastIndexOf('.'); dot >= 0; dot = host.LastIndexOf('.', dot - 1))
        {
            var labelStart = dot + 1;
            var label = host[labelStart..];
            foreach (var tld in KnownTlds)
            {
                if (label.Length <= tld.Length || !label.StartsWith(tld, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var leftover = label[tld.Length..];
                // 粘连的多余字符一般很短（QQ、qr、介绍…）；超过 3 个字符更像正常域名（example.community）
                if (leftover.Length is > 0 and <= 3 &&
                    leftover.All(c => NetCompat.IsAsciiLetterOrDigit(c) || IsCjk(c)) &&
                    !IsKnownTld(leftover))
                {
                    best = labelStart + tld.Length;
                }
            }

            if (best >= 0)
            {
                return best;
            }
        }

        return best;
    }

    private static bool IsKnownTld(string label) =>
        KnownTlds.Any(t => string.Equals(t, label, StringComparison.OrdinalIgnoreCase));

    private static bool IsCjk(char c) =>
        c is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF';

    /// <summary>返回第一个「连续 N 个中文」的起始下标，没有则 -1。</summary>
    private static int FirstCjkRun(string text, int minimumLength)
    {
        var run = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsCjk(text[i]))
            {
                run++;
                if (run >= minimumLength)
                {
                    return i - run + 1;
                }
            }
            else
            {
                run = 0;
            }
        }

        return -1;
    }
}
