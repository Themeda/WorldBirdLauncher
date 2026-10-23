using System.Linq;
using McServerList.Core.Models;

namespace McServerList.Core.Launcher;

/// <summary>
/// 模组加载器 × 游戏版本 × Java 的适配拦截（用户 2026-10-08：「要做实际的界面拦截」）。
///
/// **依据（实测 + 官方 meta 的 profile JSON 采样）**：
/// Fabric / Quilt 的 loader 自带 ASM，而 ASM 只能读"不高于它支持的 class 文件版本"的类。
/// 在 Java 25 上 Mixin 会去解析 JDK 的类（class 文件主版本 69），旧 ASM 直接抛
/// <c>Unsupported class file major version 69</c> → <c>MixinPreProcessorException ... from mod iris</c>。
/// 采样（game=1.20.1）：
/// <list type="bullet">
///   <item>fabric-loader 0.14.25 / 0.15.x / 0.16.0–0.16.5 → ASM 9.6（最高 Java 22）</item>
///   <item>fabric-loader 0.16.9 → ASM 9.7.1（Java 24）；0.17.0 → ASM 9.8（Java 25）；0.18+ → 9.9/9.10</item>
///   <item>quilt-loader 0.20.0 → ASM 9.5（Java 21）；0.23.x / 0.24.0 → ASM 9.6（Java 22）</item>
/// </list>
/// 老 Forge（游戏版本 &lt; 1.13）必须 Java 8；NeoForge 至少 Java 17。
/// </summary>
public static class LoaderCompatibility
{
    /// <summary>Fabric / Quilt 这类"按 loader 版本决定 ASM"的加载器，能用的最高 Java 主版本。</summary>
    public static int MaxSupportedJava(string loader, string loaderVersion)
    {
        var version = VersionKey(loaderVersion);

        return loader switch
        {
            // ASM 9.8 → class 69（Java 25）；9.7.1 → 68（Java 24）；9.6 → 66（Java 22）
            "Fabric" => version >= VersionKey("0.17.0") ? 25
                : version >= VersionKey("0.16.9") ? 24
                : 22,
            // Quilt 的 loader 更新较慢：最新的 0.24.0 仍只有 ASM 9.6
            "Quilt" => version >= VersionKey("0.24.0") ? 22 : 21,
            _ => int.MaxValue,
        };
    }

    /// <summary>NeoForge / 新版 Forge 要求的最低 Java。</summary>
    public static int MinRequiredJava(string loader, string gameVersion) => loader switch
    {
        "NeoForge" => 17,
        _ => 8,
    };

    /// <summary>这套（加载器 / 加载器版本 / 游戏版本 / Java）能不能用。</summary>
    public static (bool Ok, string? Reason) Check(
        string loader, string? loaderVersion, string gameVersion, int javaMajor)
    {
        if (string.IsNullOrWhiteSpace(loaderVersion) || loader is "不安装" or "原版")
        {
            return (true, null);
        }

        // 老 Forge（1.12.2 及更早，LWJGL2 那代）只有 Java 8 能跑
        if (loader == "Forge" && IsLegacyForge(gameVersion) && javaMajor != 8)
        {
            return (false, $"老版 Forge（{gameVersion}）只能用 Java 8，当前是 Java {javaMajor}");
        }

        var min = MinRequiredJava(loader, gameVersion);
        if (javaMajor < min)
        {
            return (false, $"{loader} 至少需要 Java {min}，当前是 Java {javaMajor}");
        }

        var max = MaxSupportedJava(loader, loaderVersion!);
        if (javaMajor > max)
        {
            return (false, $"{loader} {loaderVersion} 只能用到 Java {max}，"
                           + $"当前 Java {javaMajor} 会报 Unsupported class file major version（实测）");
        }

        return (true, null);
    }

    /// <summary>游戏版本是不是 1.13 之前（老 Forge / LWJGL2 那代）。</summary>
    public static bool IsLegacyForge(string? gameVersion) =>
        VersionCompatibility.TryParseVersionKey(gameVersion, out var key) && key < 1.13;

    /// <summary>把 x.y.z 压成一个可比较的整数（0.17.0 → 17，0.16.9 → 1609）。</summary>
    private static int VersionKey(string? version)
    {
        var parts = (version ?? string.Empty).Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            var text = new string(parts[i].TakeWhile(c => c is >= '0' and <= '9').ToArray());
            if (!int.TryParse(text, out numbers[i]))
            {
                numbers[i] = 0;
            }
        }

        return numbers[0] * 10000 + numbers[1] * 100 + numbers[2];
    }
}
