using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace McServerList.Core.Protocol;

/// <summary>
/// MOTD / 聊天组件的文本处理。
///
/// 两类用法：
/// ① <see cref="Flatten"/> / <see cref="Normalize"/>：取"纯文本"（筛选、搜索、日志用）；
/// ② <see cref="FlattenFormatted"/> / <see cref="ParseSegments"/>：**保留**颜色与格式（MOTD 展示用）。
///    现代服务器大多用 JSON 文本组件，颜色写在 <c>color</c> 字段里；只有
///    <see cref="FlattenFormatted"/> 会把它还原成 <c>§</c> 代码（2026-10-08 付费模块需求）。
/// </summary>
public static class MinecraftText
{
    /// <summary>把 description 字段（字符串 / 对象 / 数组）展开为纯文本（格式代码全部丢掉）。</summary>
    public static string Flatten(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                {
                    var builder = new StringBuilder();
                    foreach (var item in element.EnumerateArray())
                    {
                        builder.Append(Flatten(item));
                    }

                    return builder.ToString();
                }

            case JsonValueKind.Object:
                {
                    var builder = new StringBuilder();
                    if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }

                    if (element.TryGetProperty("extra", out var extra))
                    {
                        builder.Append(Flatten(extra));
                    }

                    return builder.ToString();
                }

            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 把 description 字段展开为**带 § 颜色/格式代码**的文本（MOTD 卡片 / 详情展示用）。
    /// 支持字符串、数组与 <c>{text, extra, color, bold, italic, underlined, strikethrough}</c> 对象。
    /// </summary>
    public static string FlattenFormatted(JsonElement element) =>
        FlattenFormatted(element, TextStyle.Default, new StyleState());

    /// <summary>一段带格式的文本；<see cref="Color"/> 为 <c>#RRGGBB</c>，null 表示"没指定、跟随主题"。</summary>
    public sealed record Segment(string Text, string? Color, bool Bold, bool Italic, bool Underline, bool Strikethrough);

    /// <summary>
    /// 把带 § 代码的文本切成"文字 + 样式"的段落序列（WPF 用 Inlines 渲染；<c>\n</c> 保留在文字里）。
    /// 支持 <c>§0</c>–<c>§f</c>、<c>§l/§o/§n/§m/§r</c>、<c>§#RRGGBB</c> 与 <c>§x§R§R§G§G§B§B</c>。
    /// </summary>
    public static IReadOnlyList<Segment> ParseSegments(string? text)
    {
        var segments = new List<Segment>();
        // net48/netstandard2.0 的可空注解比 net8 弱：先落到非空局部变量，避免 CS8602（见 AGENTS 坑 36）
        text = text ?? string.Empty;
        if (text.Length == 0)
        {
            return segments;
        }

        var style = TextStyle.Default;
        var builder = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Section)
            {
                builder.Append(text[i]);
                continue;
            }

            // §x§R§R§G§G§B§B：共 14 个字符，逐对校验
            if (i + 13 < text.Length && char.ToLowerInvariant(text[i + 1]) == 'x' && IsHexPairs(text, i + 2))
            {
                var hex = new StringBuilder(6);
                for (var k = 0; k < 6; k++)
                {
                    hex.Append(text[i + 3 + k * 2]);
                }

                Flush(segments, builder, style);
                style = style with { Color = "#" + hex.ToString().ToUpperInvariant() };
                i += 13;
                continue;
            }

            // §#RRGGBB
            if (i + 7 < text.Length && text[i + 1] == '#' && IsHex(text, i + 2, 6))
            {
                Flush(segments, builder, style);
                style = style with { Color = "#" + text.Substring(i + 2, 6).ToUpperInvariant() };
                i += 7;
                continue;
            }

            if (i + 1 >= text.Length)
            {
                break;
            }

            var code = char.ToLowerInvariant(text[i + 1]);
            i++;
            var updated = ApplyCode(style, code);
            if (updated is null)
            {
                continue; // §k（乱码）等：丢掉代码本身，文字照留
            }

            if (updated.Value != style)
            {
                Flush(segments, builder, style);
                style = updated.Value;
            }
        }

        Flush(segments, builder, style);
        return segments;
    }

    /// <summary>只去掉 §k（乱码）代码，保留颜色与其它格式。</summary>
    public static string KeepFormatting(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : ObfuscatedRegex.Replace(text, string.Empty);

    /// <summary>去除 § 颜色/格式代码。</summary>
    public static string StripFormatting(string text) => FormattingCodeRegex.Replace(text, string.Empty);

    /// <summary>去除格式代码并压缩空白，用于列表展示（会丢掉换行——MOTD 别用这个）。</summary>
    public static string Normalize(string text)
    {
        var stripped = StripFormatting(text);
        var collapsed = WhitespaceRegex.Replace(stripped, " ").Trim();
        return collapsed;
    }

    private const char Section = '\u00a7';

    /// <summary>
    /// 展开成"带 § 代码"的连续文本流。
    ///
    /// 关键点：颜色/格式代码是**流式**生效的，`extra` 里的兄弟节点各自从**父节点**继承样式、
    /// 不继承前一个兄弟的样式。所以必须用一个"当前已发出的样式"上下文（<see cref="StyleState"/>）
    /// 在样式变化时补发代码——只按"和父节点的差"发代码，兄弟样式回退时颜色就会串。
    /// </summary>
    private static string FlattenFormatted(JsonElement element, TextStyle inherited, StyleState state)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return Emit(inherited, state) + (element.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                return element.GetRawText();
            case JsonValueKind.Array:
                {
                    var builder = new StringBuilder();
                    foreach (var item in element.EnumerateArray())
                    {
                        builder.Append(FlattenFormatted(item, inherited, state));
                    }

                    return builder.ToString();
                }

            case JsonValueKind.Object:
                {
                    var style = MergeStyle(inherited, element);
                    var builder = new StringBuilder();
                    builder.Append(Emit(style, state));
                    if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }

                    if (element.TryGetProperty("extra", out var extra))
                    {
                        builder.Append(FlattenFormatted(extra, style, state));
                    }

                    return builder.ToString();
                }

            default:
                return string.Empty;
        }
    }

    /// <summary>发出"切到这个样式"所需的代码（样式没变就什么都不发）。</summary>
    private static string Emit(TextStyle style, StyleState state)
    {
        if (style == state.Current)
        {
            return string.Empty;
        }

        var prefix = StylePrefix(style, state.Current);
        state.Current = style;
        return prefix;
    }

    /// <summary>展开时用的"当前已发出的样式"（按引用在整棵树上传递）。</summary>
    private sealed class StyleState
    {
        public TextStyle Current { get; set; } = TextStyle.Default;
    }

    private static TextStyle MergeStyle(TextStyle parent, JsonElement element)
    {
        var style = parent;
        if (element.TryGetProperty("color", out var color) && color.ValueKind == JsonValueKind.String)
        {
            var value = color.GetString() ?? string.Empty;
            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                style = style with { Color = value.ToUpperInvariant() };
            }
            else if (NamedColors.TryGetValue(value.ToLowerInvariant(), out var hex))
            {
                style = style with { Color = hex };
            }
        }

        style = style with
        {
            Bold = ReadFlag(element, "bold", style.Bold),
            Italic = ReadFlag(element, "italic", style.Italic),
            Underline = ReadFlag(element, "underlined", style.Underline),
            Strikethrough = ReadFlag(element, "strikethrough", style.Strikethrough),
        };
        return style;
    }

    private static bool ReadFlag(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static string StylePrefix(TextStyle style, TextStyle parent)
    {
        if (style == parent)
        {
            return string.Empty;
        }

        // 只有"父样式有、子样式没有"的格式才需要 §r 复位；反过来（新增格式）直接叠加代码即可。
        var clearsFormat = (parent.Bold && !style.Bold) || (parent.Italic && !style.Italic) ||
                           (parent.Underline && !style.Underline) || (parent.Strikethrough && !style.Strikethrough);
        var builder = new StringBuilder();
        if (clearsFormat)
        {
            builder.Append(Section).Append('r');
        }

        if (style.Color is { } color &&
            (clearsFormat || !string.Equals(color, parent.Color, StringComparison.OrdinalIgnoreCase)))
        {
            builder.Append(ColorCode(color));
        }

        if (style.Bold && (clearsFormat || !parent.Bold))
        {
            builder.Append(Section).Append('l');
        }

        if (style.Italic && (clearsFormat || !parent.Italic))
        {
            builder.Append(Section).Append('o');
        }

        if (style.Underline && (clearsFormat || !parent.Underline))
        {
            builder.Append(Section).Append('n');
        }

        if (style.Strikethrough && (clearsFormat || !parent.Strikethrough))
        {
            builder.Append(Section).Append('m');
        }

        return builder.ToString();
    }

    /// <summary>把 <c>#RRGGBB</c> 转成 § 代码（命中 16 原色就单字符，否则 <c>§#RRGGBB</c>）。</summary>
    private static string ColorCode(string hex)
    {
        foreach (var pair in LegacyColors)
        {
            if (string.Equals(pair.Value, hex, StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(Section, pair.Key);
            }
        }

        return Section + hex;
    }

    private static TextStyle? ApplyCode(TextStyle style, char code)
    {
        if (LegacyColors.TryGetValue(code, out var hex))
        {
            return style with { Color = hex };
        }

        return code switch
        {
            'l' => style with { Bold = true },
            'o' => style with { Italic = true },
            'n' => style with { Underline = true },
            'm' => style with { Strikethrough = true },
            'r' => TextStyle.Default,
            'k' => null, // 乱码：不建模
            _ => style,
        };
    }

    private static void Flush(List<Segment> segments, StringBuilder builder, TextStyle style)
    {
        if (builder.Length == 0)
        {
            return;
        }

        segments.Add(new Segment(
            builder.ToString(), style.Color, style.Bold, style.Italic, style.Underline, style.Strikethrough));
        builder.Clear();
    }

    /// <summary>校验 <c>§X§X§X§X§X§X</c>（六个十六进制位，每对之间一个 §）。</summary>
    private static bool IsHexPairs(string text, int index)
    {
        for (var pair = 0; pair < 6; pair++)
        {
            var position = index + pair * 2;
            if (position + 1 >= text.Length || text[position] != Section || !IsHex(text, position + 1, 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(string text, int index, int length)
    {
        if (index < 0 || length <= 0 || index + length > text.Length)
        {
            return false;
        }

        for (var i = 0; i < length; i++)
        {
            var c = text[index + i];
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Minecraft 16 原色（§0–§f）。</summary>
    private static readonly Dictionary<char, string> LegacyColors = new()
    {
        ['0'] = "#000000", ['1'] = "#0000AA", ['2'] = "#00AA00", ['3'] = "#00AAAA",
        ['4'] = "#AA0000", ['5'] = "#AA00AA", ['6'] = "#FFAA00", ['7'] = "#AAAAAA",
        ['8'] = "#555555", ['9'] = "#5555FF", ['a'] = "#55FF55", ['b'] = "#55FFFF",
        ['c'] = "#FF5555", ['d'] = "#FF55FF", ['e'] = "#FFFF55", ['f'] = "#FFFFFF",
    };

    /// <summary>JSON 文本组件里的命名颜色。</summary>
    private static readonly Dictionary<string, string> NamedColors = new()
    {
        ["black"] = "#000000", ["dark_blue"] = "#0000AA", ["dark_green"] = "#00AA00", ["dark_aqua"] = "#00AAAA",
        ["dark_red"] = "#AA0000", ["dark_purple"] = "#AA00AA", ["gold"] = "#FFAA00", ["gray"] = "#AAAAAA",
        ["dark_gray"] = "#555555", ["blue"] = "#5555FF", ["green"] = "#55FF55", ["aqua"] = "#55FFFF",
        ["red"] = "#FF5555", ["light_purple"] = "#FF55FF", ["yellow"] = "#FFFF55", ["white"] = "#FFFFFF",
    };

    private readonly record struct TextStyle(string? Color, bool Bold, bool Italic, bool Underline, bool Strikethrough)
    {
        public static TextStyle Default { get; } = new(null, false, false, false, false);
    }

    // 不用 [GeneratedRegex]（net8 专属），改静态 Regex 以便 netstandard2.0/net48 编译
    private static readonly Regex FormattingCodeRegex = new("\u00a7.", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex ObfuscatedRegex = new("\u00a7[kK]", RegexOptions.Compiled);
}
