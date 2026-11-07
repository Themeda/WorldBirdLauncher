using System.Windows;
using System.Windows.Media;
using System.Globalization;
using Microsoft.Win32;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 界面主题（浅色 / 深色 / 跟随系统）。
///
/// 做法：<c>Styles\Theme.xaml</c> 里所有颜色引用都写成了 <c>DynamicResource</c>，
/// 这里按目标主题把 <see cref="Application.Resources"/> 里同名的画刷**整体换成新画刷**，
/// DynamicResource 会自动重新解析并刷新界面上所有控件（不用重启、不用重建窗口）。
///
/// 注意：不能改成"就地修改 SolidColorBrush.Color"——资源字典里的画刷可能已被 WPF 冻结
/// （模板封口时会 Freeze），改它必抛 InvalidOperationException。整体替换才稳。
/// </summary>
internal static class ThemeManager
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";
    public const string DefaultAccent = "#2B7FFF";

    /// <summary>浅色（默认）——偏蓝的冷色调，和 Theme.xaml 里的默认值保持一致。</summary>
    private static readonly Dictionary<string, string> LightPalette = new(StringComparer.Ordinal)
    {
        ["BgBrush"] = "#EEF3FB",
        ["SurfaceBrush"] = "#FFFFFF",
        ["SurfaceHoverBrush"] = "#E7EEF9",
        ["BorderBrush2"] = "#DAE4F2",
        ["DividerBrush"] = "#E6EDF7",
        ["TextPrimaryBrush"] = "#0F172B",
        ["TextSecondaryBrush"] = "#556180",
        ["TextMutedBrush"] = "#8395B0",
        ["AccentBrush"] = "#2B7FFF",
        ["AccentHoverBrush"] = "#155DFC",
        ["AccentSoftBrush"] = "#E6EFFF",
        ["AccentSoftHoverBrush"] = "#D6E5FF",
        ["AccentDisabledBrush"] = "#C3D6F7",
        ["SuccessBrush"] = "#009966",
        ["WarningBrush"] = "#FE9A00",
        ["DangerBrush"] = "#E7000B",
        ["ScrollThumbBrush"] = "#C2CCDB",
        ["ScrollThumbHoverBrush"] = "#98A7BE",
    };

    /// <summary>深色——深蓝底 + 亮字，保持和浅色同一套语义（强调色仍是蓝）。</summary>
    private static readonly Dictionary<string, string> DarkPalette = new(StringComparer.Ordinal)
    {
        ["BgBrush"] = "#0F1626",
        ["SurfaceBrush"] = "#172034",
        ["SurfaceHoverBrush"] = "#1F2B41",
        ["BorderBrush2"] = "#2C3B55",
        ["DividerBrush"] = "#243149",
        ["TextPrimaryBrush"] = "#E9EFF9",
        ["TextSecondaryBrush"] = "#AAB9CF",
        ["TextMutedBrush"] = "#7E90A9",
        ["AccentBrush"] = "#4C93FF",
        ["AccentHoverBrush"] = "#79AEFF",
        ["AccentSoftBrush"] = "#1D2C49",
        ["AccentSoftHoverBrush"] = "#243659",
        ["AccentDisabledBrush"] = "#2E4062",
        ["SuccessBrush"] = "#34C88A",
        ["WarningBrush"] = "#F2A93B",
        ["DangerBrush"] = "#FF5C5C",
        ["ScrollThumbBrush"] = "#33425C",
        ["ScrollThumbHoverBrush"] = "#46597A",
    };

    /// <summary>当前生效的主题（已解析成 light / dark，不会是 system）。</summary>
    public static string Applied { get; private set; } = Light;

    /// <summary>把设置里的主题值（light / dark / system）应用成实际配色。</summary>
    public static void Apply(string? mode, string? accentColor = null)
    {
        var effective = Resolve(mode);
        Applied = effective;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var palette = new Dictionary<string, string>(
            effective == Dark ? DarkPalette : LightPalette,
            StringComparer.Ordinal);
        var accent = ParseAccent(accentColor);
        var white = Color.FromRgb(255, 255, 255);
        var black = Color.FromRgb(0, 0, 0);
        var darkSurface = Color.FromRgb(15, 22, 38);
        var surface = effective == Dark
            ? Blend(accent, darkSurface, 0.72)
            : Blend(accent, white, 0.88);
        var shellText = ContrastText(accent);

        palette["BgBrush"] = ToHex(accent);
        palette["SurfaceBrush"] = ToHex(surface);
        palette["SurfaceHoverBrush"] = ToHex(effective == Dark
            ? Blend(accent, darkSurface, 0.64)
            : Blend(accent, white, 0.78));
        palette["BorderBrush2"] = ToHex(Blend(surface, effective == Dark ? white : black, 0.16));
        palette["DividerBrush"] = ToHex(Blend(surface, effective == Dark ? white : black, 0.09));
        palette["ShellTextPrimaryBrush"] = ToHex(shellText);
        palette["ShellTextSecondaryBrush"] = ToHex(Blend(shellText, accent, 0.22));
        palette["ShellTextMutedBrush"] = ToHex(Blend(shellText, accent, 0.42));
        palette["TextPrimaryBrush"] = effective == Dark ? "#F8FAFF" : "#0F172B";
        palette["TextSecondaryBrush"] = effective == Dark ? "#B8C6DB" : "#556180";
        palette["TextMutedBrush"] = effective == Dark ? "#8EA2BC" : "#8395B0";
        palette["AccentBrush"] = ToHex(accent);
        palette["AccentHoverBrush"] = ToHex(effective == Dark
            ? Blend(accent, white, 0.18)
            : Blend(accent, Color.FromRgb(0, 0, 0), 0.16));
        palette["AccentSoftBrush"] = ToHex(Blend(accent, surface, 0.84));
        palette["AccentSoftHoverBrush"] = ToHex(Blend(accent, surface, 0.74));
        palette["AccentDisabledBrush"] = ToHex(Blend(accent, surface, 0.62));

        foreach (var (key, hex) in palette)
        {
            var color = ParseColor(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush; // DynamicResource 会自动跟着刷新
        }
    }

    /// <summary>把设置值解析成 light / dark（system 时读注册表里的应用主题）。</summary>
    public static string Resolve(string? mode) => (mode ?? Light).Trim().ToLowerInvariant() switch
    {
        Dark => Dark,
        System => IsSystemDark() ? Dark : Light,
        _ => Light,
    };

    public static bool TryNormalizeAccent(string? value, out string normalized)
    {
        normalized = DefaultAccent;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hex = (value ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        normalized = "#" + hex.ToUpperInvariant();
        return true;
    }

    private static Color ParseAccent(string? value) =>
        ParseColor(TryNormalizeAccent(value, out var normalized) ? normalized : DefaultAccent);

    private static Color ParseColor(string value) =>
        (Color)ColorConverter.ConvertFromString(value);

    private static Color Blend(Color from, Color to, double amount)
    {
        var weight = Math.Max(0, Math.Min(1, amount));
        return Color.FromRgb(
            (byte)Math.Round(from.R * (1 - weight) + to.R * weight),
            (byte)Math.Round(from.G * (1 - weight) + to.G * weight),
            (byte)Math.Round(from.B * (1 - weight) + to.B * weight));
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ContrastText(Color background)
    {
        var luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255;
        return luminance > 0.58 ? Color.FromRgb(15, 23, 43) : Colors.White;
    }

    /// <summary>Windows「个性化 → 颜色 → 选择默认应用模式」；读不到时按浅色。</summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
