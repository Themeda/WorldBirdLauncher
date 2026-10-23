using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using McServerList.Core.Text;

namespace McServerList.App.Views;

/// <summary>
/// 支持少量行内标记的 TextBlock：
///   - HTML：&lt;b&gt; / &lt;i&gt; / &lt;u&gt; / &lt;br&gt; / &lt;a href="…"&gt;文字&lt;/a&gt;
///   - Markdown：[文字](链接)（由 MarkdownSupport 转成 &lt;a&gt;）
///   - 裸 http(s) URL
///
/// 2026-10-21 用户反馈：长介绍里的 `<a href="…">查看链接</a>` 之前被当成普通文字，
/// 现在会保留链接文字并把点击交给系统浏览器；`javascript:` / `file:` 等非 http(s) 链接只显示文字。
/// </summary>
public sealed class MarkupTextBlock : TextBlock
{
    /// <summary>点击正文中的 http(s) 链接时触发（参数为网址）。</summary>
    public event EventHandler<string>? LinkClicked;

    public static readonly DependencyProperty MarkupProperty = DependencyProperty.Register(
        nameof(Markup),
        typeof(string),
        typeof(MarkupTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>相对链接的基准地址（通常是服务器条目的 sourceUrl）。</summary>
    public static readonly DependencyProperty BaseUrlProperty = DependencyProperty.Register(
        nameof(BaseUrl),
        typeof(string),
        typeof(MarkupTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public string Markup
    {
        get => (string)GetValue(MarkupProperty);
        set => SetValue(MarkupProperty, value);
    }

    public string BaseUrl
    {
        get => (string)GetValue(BaseUrlProperty);
        set => SetValue(BaseUrlProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MarkupTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        // 先折 markdown，再整体解 HTML 实体：历史数据里存在 &lt;a href="…"&gt; 这类转义。
        var text = WebUtility.HtmlDecode(MarkdownSupport.ToMarkup(Markup ?? string.Empty));
        if (text.Length == 0)
        {
            return;
        }

        var bold = 0;
        var italic = 0;
        var underline = 0;
        var position = 0;
        string? linkHref = null;
        var linkText = new StringBuilder();

        foreach (Match match in Regex.Matches(text, @"<[^>]*>", RegexOptions.Singleline))
        {
            if (match.Index > position)
            {
                var chunk = text[position..match.Index];
                if (linkHref is not null)
                {
                    linkText.Append(chunk);
                }
                else
                {
                    AddRun(chunk, bold > 0, italic > 0, underline > 0);
                }
            }

            var tagMatch = Regex.Match(match.Value, @"^<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9]*)",
                RegexOptions.Singleline);
            if (tagMatch.Success)
            {
                var closing = tagMatch.Groups[1].Value.Length > 0;
                var name = tagMatch.Groups[2].Value.ToLowerInvariant();
                switch (name)
                {
                    case "b":
                        bold = closing ? Math.Max(0, bold - 1) : bold + 1;
                        break;
                    case "i":
                        italic = closing ? Math.Max(0, italic - 1) : italic + 1;
                        break;
                    case "u":
                        underline = closing ? Math.Max(0, underline - 1) : underline + 1;
                        break;
                    case "br":
                        if (linkHref is not null)
                        {
                            linkText.Append('\n');
                        }
                        else
                        {
                            Inlines.Add(new LineBreak());
                        }
                        break;
                    case "a":
                        if (!closing)
                        {
                            linkHref = NormalizeHref(match.Value);
                            linkText.Clear();
                        }
                        else
                        {
                            var label = linkText.ToString().Trim();
                            if (linkHref is not null && label.Length > 0)
                            {
                                AddHyperlink(label, linkHref, bold > 0, italic > 0, underline > 0);
                            }
                            else if (label.Length > 0)
                            {
                                AddRun(label, bold > 0, italic > 0, underline > 0);
                            }

                            linkHref = null;
                            linkText.Clear();
                        }
                        break;
                    default:
                        // 块级标签补一个换行，其它未知标签直接剥掉（与旧行为一致）
                        if (!closing && Inlines.Count > 0 &&
                            name is "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                        {
                            Inlines.Add(new LineBreak());
                        }
                        break;
                }
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            var rest = text[position..];
            if (linkHref is not null)
            {
                linkText.Append(rest);
            }
            else
            {
                AddRun(rest, bold > 0, italic > 0, underline > 0);
            }
        }

        // 未闭合的 <a>：按普通文字显示，别把链接文字丢掉
        if (linkHref is not null && linkText.Length > 0)
        {
            AddRun(linkText.ToString(), bold > 0, italic > 0, underline > 0);
        }
    }

    private string? NormalizeHref(string tag)
    {
        var hrefMatch = Regex.Match(tag,
            @"\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!hrefMatch.Success)
        {
            // 用户 2026-10-21 确认：没有 href（或只有 javascript:void(0)）时，
            // 链接文字仍指向该条目的来源页，而不是退化成不可点的普通文字。
            return NormalizeWebUrl(BaseUrl);
        }

        var raw = WebUtility.HtmlDecode(
            hrefMatch.Groups[1].Success ? hrefMatch.Groups[1].Value :
            hrefMatch.Groups[2].Success ? hrefMatch.Groups[2].Value :
            hrefMatch.Groups[3].Value).Trim();
        if (raw.Length == 0)
        {
            return NormalizeWebUrl(BaseUrl);
        }

        if (raw.StartsWith("//", StringComparison.Ordinal))
        {
            raw = "https:" + raw;
        }

        // MC百科会把站外地址包成 //link.mcmod.cn/target/<base64>。
        // 历史数据里可能直接保留这个跳转地址，客户端也应解出真实目标。
        if (Uri.TryCreate(raw, UriKind.Absolute, out var redirect) &&
            string.Equals(redirect.Host, "link.mcmod.cn", StringComparison.OrdinalIgnoreCase))
        {
            var segments = redirect.AbsolutePath.Trim('/').Split('/');
            if (segments.Length == 2 &&
                string.Equals(segments[0], "target", StringComparison.OrdinalIgnoreCase) &&
                TryDecodeBase64Url(segments[1], out var decoded) &&
                NormalizeWebUrl(decoded) is { } decodedUrl)
            {
                return decodedUrl;
            }
        }

        var normalized = NormalizeWebUrl(raw);
        if (normalized is not null)
        {
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(BaseUrl) &&
            Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, raw, out var resolved) &&
            (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
        {
            return resolved.ToString();
        }

        return NormalizeWebUrl(BaseUrl);
    }

    private static string? NormalizeWebUrl(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private static bool TryDecodeBase64Url(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Trim();
            return decoded.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void AddRun(string content, bool bold, bool italic, bool underline)
    {
        if (content.Length == 0)
        {
            return;
        }

        // 文本里出现的裸网址也做成可点击链接
        var position = 0;
        foreach (var match in UrlText.Find(content))
        {
            if (match.Start > position)
            {
                AddPlainRun(content[position..match.Start], bold, italic, underline);
            }

            var url = match.Url;
            AddHyperlink(url, url, bold, italic, underline);

            if (match.Rest.Length > 0)
            {
                AddPlainRun(match.Rest, bold, italic, underline);
            }

            position = match.Start + match.Length;
        }

        if (position < content.Length)
        {
            AddPlainRun(content[position..], bold, italic, underline);
        }
    }

    private void AddHyperlink(string text, string url, bool bold, bool italic, bool underline)
    {
        if (text.Length == 0)
        {
            return;
        }

        var link = new Hyperlink(new Run(text))
        {
            NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null,
            Foreground = System.Windows.Media.Brushes.DodgerBlue,
            TextDecorations = System.Windows.TextDecorations.Underline,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        };
        link.RequestNavigate += (_, e) =>
        {
            e.Handled = true;
            LinkClicked?.Invoke(this, url);
        };
        Inlines.Add(link);
    }

    private void AddPlainRun(string content, bool bold, bool italic, bool underline)
    {
        if (content.Length == 0)
        {
            return;
        }

        var run = new Run(content)
        {
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        };
        if (underline)
        {
            run.TextDecorations = System.Windows.TextDecorations.Underline;
        }

        Inlines.Add(run);
    }
}
