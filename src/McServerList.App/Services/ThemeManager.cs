using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace McServerList.App.Services;

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
    public static void Apply(string? mode)
    {
        var effective = Resolve(mode);
        Applied = effective;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var palette = effective == Dark ? DarkPalette : LightPalette;
        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
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
