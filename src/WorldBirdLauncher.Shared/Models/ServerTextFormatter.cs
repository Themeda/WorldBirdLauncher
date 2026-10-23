using System.Text;
using System.Text.RegularExpressions;

namespace WorldBirdLauncher.Core.Models;

/// <summary>
/// 服务器文本清洗与排版：
/// ① 删除 Minecraft 颜色/格式代码（<c>§a</c>、<c>&amp;a</c>、<c>§#RRGGBB</c>、<c>§x§R§R§G§G§B§B</c>）；
/// ② 把来源站点抓来的一整行简介重新拆成有换行的段落（保留空行与条目符号）。
/// 两个功能都做成通用方法，任何数据源都能复用。
/// </summary>
public static class ServerTextFormatter
{
    // 注意：这里不用 [GeneratedRegex]（net8 才有）。为了 netstandard2.0/net48 也能编译，
    // 统一改成静态 Regex（加 Compiled，行为一致、只是首次匹配多几毫秒）。
    private static readonly Regex ColorCodeRegex =
        new(@"[\u00a7&][0-9aAbBcCdDeEfFkKlLmMnNoOrRxX]", RegexOptions.Compiled);

    private static readonly Regex ExtraBlankLinesRegex = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>删除颜色与格式代码（含 §#RRGGBB 与 §x§R… 这类新写法）。</summary>
    public static string StripColorCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 先把 §x§R§R§G§G§B§B（16 位色）整体吃掉
        var cleaned = Regex.Replace(text, @"[\u00a7&]x(?:[\u00a7&][0-9a-fA-F]){6}", string.Empty);
        cleaned = Regex.Replace(cleaned, @"[\u00a7&]#[0-9a-fA-F]{6}", string.Empty);
        cleaned = ColorCodeRegex.Replace(cleaned, string.Empty);

        // 颜色码被删掉后可能留下多余空格（如 "§c 维护" → " 维护"）
        return Regex.Replace(cleaned, @"[ \t]{2,}", " ").Trim();
    }

    /// <summary>
    /// 把一整行长文本重新分段：在常见条目符号/表情/编号前断行，过长的句子按标点折行。
    /// </summary>
    public static string ToParagraphs(string? text, int maxLineLength = 90)
    {
        var cleaned = StripColorCodes(text);
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(cleaned.Length + 64);
        var lineLength = 0;
        for (var i = 0; i < cleaned.Length; i++)
        {
            var current = cleaned[i];

            // 已有的换行照抄，并重置行宽
            if (current == '\n')
            {
                builder.Append('\n');
                lineLength = 0;
                continue;
            }

            // 遇到条目符号/表情/编号：如果当前行已有内容，先换行
            if (lineLength > 0 && IsSectionMarker(cleaned, i))
            {
                builder.Append('\n');
                lineLength = 0;
            }

            builder.Append(current);
            lineLength++;

            // 句子结束且当前行够长 → 折行
            if (lineLength >= maxLineLength && IsSentenceEnd(current))
            {
                builder.Append('\n');
                lineLength = 0;
            }
        }

        var result = builder.ToString();
        result = ExtraBlankLinesRegex.Replace(result, "\n\n");
        return result.Trim();
    }

    private static bool IsSentenceEnd(char c) => c is '。' or '！' or '？' or '；' or '.' or '!' or '?';

    private static bool IsSectionMarker(string text, int index)
    {
        var c = text[index];

        // 常见条目符号
        if (c is '【' or '●' or '◆' or '■' or '★' or '☆' or '▶' or '♦' or '➤')
        {
            return true;
        }

        // 带圈数字 ①-⑳（含代理对外的 BMP 范围）
        if (c is >= '\u2460' and <= '\u2473')
        {
            return true;
        }

        // emoji 起始（代理对高位）
        if (char.IsHighSurrogate(c))
        {
            return true;
        }

        // 行内 "1." "2、" 这类编号（要求前面是句号/分号/空格，避免切断小数与网址）
        if (char.IsDigit(c))
        {
            var previous = index == 0 ? '\0' : text[index - 1];
            var isBoundary = index == 0 || previous is '。' or '；' or ' ' or '　' or '）' or ')' or '\n';
            if (!isBoundary)
            {
                return false;
            }

            var j = index;
            while (j < text.Length && char.IsDigit(text[j]))
            {
                j++;
            }

            return j < text.Length && (text[j] == '.' || text[j] == '、' || text[j] == '）' || text[j] == ')');
        }

        return false;
    }
}
