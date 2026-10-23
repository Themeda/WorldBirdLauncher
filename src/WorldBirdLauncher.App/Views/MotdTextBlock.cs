using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WorldBirdLauncher.Core.Protocol;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// MOTD 展示控件：把带 § 颜色/格式代码的 MOTD 渲染成"彩色 + 换行"的文本
/// （需求文档决定：客户端卡片与详情页都要显示 MOTD，并解析颜色代码与换行）。
///
/// 颜色策略（用户 2026-10-08 确认）：
/// - 没写颜色的部分跟随当前主题的前景色；
/// - 写了颜色的按原色渲染；但**纯白/纯黑**会和当前主题撞色（浅色主题下的白字看不见），
///   这两种"默认色"自动换成本主题的前景色，其余原色照旧。
/// </summary>
public sealed class MotdTextBlock : TextBlock
{
    public static readonly DependencyProperty MotdProperty = DependencyProperty.Register(
        nameof(Motd),
        typeof(string),
        typeof(MotdTextBlock),
        new PropertyMetadata(null, OnMotdChanged));

    public string? Motd
    {
        get => (string?)GetValue(MotdProperty);
        set => SetValue(MotdProperty, value);
    }

    private static void OnMotdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MotdTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();
        var segments = MinecraftText.ParseSegments(Motd);
        if (segments.Count == 0)
        {
            Text = string.Empty;
            return;
        }

        var baseColor = (Foreground as SolidColorBrush)?.Color ?? Colors.Gray;
        foreach (var segment in segments)
        {
            var brush = ResolveBrush(segment.Color, baseColor);
            // 换行：MOTD 里的 \n 要真的换行（需求要求解析换行）
            var parts = segment.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    Inlines.Add(new LineBreak());
                }

                if (parts[i].Length == 0)
                {
                    continue;
                }

                var run = new Run(parts[i]);
                if (segment.Bold)
                {
                    run.FontWeight = FontWeights.Bold;
                }

                if (segment.Italic)
                {
                    run.FontStyle = FontStyles.Italic;
                }

                if (segment.Underline)
                {
                    // 注意：不能写 TextDecorations.Underline —— 在 TextBlock 子类里这个名字会绑到
                    // 继承来的 TextDecorations 属性（TextDecorationCollection），net48 直接编译不过
                    run.TextDecorations = System.Windows.TextDecorations.Underline;
                }
                else if (segment.Strikethrough)
                {
                    run.TextDecorations = System.Windows.TextDecorations.Strikethrough;
                }

                if (brush is not null)
                {
                    run.Foreground = brush;
                }

                Inlines.Add(run);
            }
        }
    }

    /// <summary>把 #RRGGBB 变成画刷；纯白/纯黑在撞色时返回 null（跟随主题前景色）。</summary>
    private static Brush? ResolveBrush(string? hex, Color baseColor)
    {
        // net48 的可空注解比 net8 弱：先落到非空局部变量（AGENTS 坑 36）
        var text = hex ?? string.Empty;
        if (text.Length != 7 || text[0] != '#')
        {
            return null;
        }

        if (!TryParse(text.Substring(1), out var color))
        {
            return null;
        }

        var baseIsLight = Luminance(baseColor) > 0.55;
        if (color == Colors.White && !baseIsLight)
        {
            return null; // 深色主题下的白字 = 默认色，跟随主题
        }

        if (color == Colors.Black && baseIsLight)
        {
            return null; // 浅色主题下的黑字 = 默认色，跟随主题
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static bool TryParse(string hex, out Color color)
    {
        color = Colors.Gray;
        try
        {
            var value = System.Convert.ToUInt32(hex, 16);
            color = Color.FromRgb((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
}
