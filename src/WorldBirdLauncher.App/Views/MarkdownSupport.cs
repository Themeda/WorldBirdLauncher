using System.Text.RegularExpressions;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 长介绍的 markdown 子集 → 本控件支持的标记（粗体/斜体/下划线/换行），**HTML 标签原样保留**。
///
/// 支持：`# 标题`（→ 加粗一行）、`**粗体**`、`*斜体*`/`_斜体_`、`` `行内代码` ``（→ 去反引号）、
/// `[文字](链接)`（→ `<a href="链接">文字</a>`）、`![说明](链接)`（→ `[图片：说明] 链接`）、
/// `- / * 列表项`（→ `• `）、`> 引用`（→ `▎`）、`---` 分隔线、以及普通换行。
/// 没写 markdown 的纯文本 / 带 HTML 标签的老数据不受影响。
/// </summary>
internal static class MarkdownSupport
{
    public static string ToMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = text.Replace("\r\n", "\n").Replace("\r", "\n");

        // 图片：![说明](链接)
        value = ImageRegex.Replace(value, m => $"[图片：{m.Groups[1].Value.Trim()}] {m.Groups[2].Value}");
        // 链接：[文字](链接) → 标准 <a>；MarkupTextBlock 会保留“文字”作为可点击链接
        value = LinkRegex.Replace(value, m => $"<a href=\"{m.Groups[2].Value}\">{m.Groups[1].Value}</a>");
        // 行内代码：`code` → code
        value = InlineCodeRegex.Replace(value, "$1");
        // 粗体 / 斜体
        value = BoldRegex.Replace(value, "<b>$1</b>");
        value = ItalicRegex.Replace(value, "<i>$1</i>");

        var lines = value.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("###", StringComparison.Ordinal) ||
                trimmed.StartsWith("##", StringComparison.Ordinal) ||
                trimmed.StartsWith('#'))
            {
                var title = trimmed.TrimStart('#').Trim();
                lines[i] = $"<b>{title}</b>";
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                lines[i] = "• " + trimmed[2..];
            }
            else if (trimmed.StartsWith("> ", StringComparison.Ordinal))
            {
                lines[i] = "▎" + trimmed[2..];
            }
            else if (trimmed is "---" or "***" or "___")
            {
                lines[i] = "────────────";
            }
        }

        return string.Join("\n", lines);
    }

    // net48 上没有 [GeneratedRegex]（源生成器 + 那个特性都是 net7+ 的），
    // 按本项目既有约定改成静态编译好的 Regex。
    private static readonly Regex ImageRegex =
        new(@"!\[([^\]]*)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    private static readonly Regex LinkRegex =
        new(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);

    private static readonly Regex InlineCodeRegex =
        new(@"`([^`]+)`", RegexOptions.Compiled);

    private static readonly Regex BoldRegex =
        new(@"\*\*([^*\n]+)\*\*", RegexOptions.Compiled);

    private static readonly Regex ItalicRegex =
        new(@"(?<![*\w])\*([^*\n]+)\*(?![*\w])", RegexOptions.Compiled);
}
