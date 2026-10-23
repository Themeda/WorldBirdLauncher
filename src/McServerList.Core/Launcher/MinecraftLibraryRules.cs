using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>
/// 版本 JSON 里 libraries 的公共判定逻辑（完整性校验与安装进度统计共用）：
/// 按 <c>rules</c> 过滤出"本机适用"的库，并解析出它在本机需要的具体文件。
/// </summary>
public static class MinecraftLibraryRules
{
    private static readonly string CurrentOsName =
        NetCompat.IsWindows() ? "windows" :
        NetCompat.IsMacOS() ? "osx" : "linux";

    /// <summary>按 rules 判断是否适用于当前系统（默认与官方启动器一致）。</summary>
    public static bool IsAllowedForThisSystem(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array ||
            rules.GetArrayLength() == 0)
        {
            return true;
        }

        var allowed = false; // 有 rules 但没有规则命中 ⇒ 不允许
        foreach (var rule in rules.EnumerateArray())
        {
            if (!RuleMatches(rule))
            {
                continue;
            }

            allowed = rule.TryGetProperty("action", out var action) &&
                      string.Equals(action.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    /// <summary>取这个库在当前系统下真正需要的文件（artifact 或当前系统的 classifiers）。</summary>
    public static (string Path, string? Sha1, long Size)? ResolveArtifact(JsonElement library)
    {
        if (!library.TryGetProperty("downloads", out var downloads))
        {
            return null;
        }

        if (downloads.TryGetProperty("artifact", out var artifact) &&
            artifact.ValueKind == JsonValueKind.Object &&
            artifact.TryGetProperty("path", out var artifactPath) &&
            artifactPath.GetString() is { Length: > 0 } path)
        {
            return (path,
                artifact.TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null,
                artifact.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue) ? sizeValue : 0);
        }

        if (!downloads.TryGetProperty("classifiers", out var classifiers) || classifiers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in ClassifierKeys())
        {
            if (classifiers.TryGetProperty(key, out var classifier) &&
                classifier.TryGetProperty("path", out var classifierPath) &&
                classifierPath.GetString() is { Length: > 0 } classifierRelative)
            {
                return (classifierRelative,
                    classifier.TryGetProperty("sha1", out var classifierSha1) ? classifierSha1.GetString() : null,
                    classifier.TryGetProperty("size", out var classifierSize) && classifierSize.TryGetInt64(out var classifierSizeValue)
                        ? classifierSizeValue
                        : 0);
            }
        }

        return null;
    }

    /// <summary>
    /// 列出这个库在本机需要的**全部**文件：主 artifact（若有）+ 当前系统的 natives classifier（若有）。
    ///
    /// 为什么两个都要：1.16.x / 1.17.x 那一代的官方版本 JSON 里，同一个 LWJGL 库会出现**两条同名条目**
    /// ——一条只有 artifact，另一条带 classifiers + natives。只取 artifact 会让 natives 包永远进不了下载清单
    /// （全新安装时没人下它，启动就报 Failed to locate library: lwjgl.dll）。
    /// </summary>
    public static IEnumerable<(string Path, string? Url, string? Sha1, long Size)> ResolveFiles(JsonElement library)
    {
        if (!library.TryGetProperty("downloads", out var downloads) || downloads.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (downloads.TryGetProperty("artifact", out var artifact) &&
            artifact.ValueKind == JsonValueKind.Object &&
            artifact.TryGetProperty("path", out var artifactPath) &&
            artifactPath.GetString() is { Length: > 0 } path)
        {
            yield return (path,
                artifact.TryGetProperty("url", out var artifactUrl) ? artifactUrl.GetString() : null,
                artifact.TryGetProperty("sha1", out var artifactSha1) ? artifactSha1.GetString() : null,
                artifact.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue) ? sizeValue : 0);
        }

        if (!downloads.TryGetProperty("classifiers", out var classifiers) || classifiers.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var key in NativesClassifierKeys(library))
        {
            if (classifiers.TryGetProperty(key, out var classifier) &&
                classifier.ValueKind == JsonValueKind.Object &&
                classifier.TryGetProperty("path", out var classifierPath) &&
                classifierPath.GetString() is { Length: > 0 } classifierRelative)
            {
                yield return (classifierRelative,
                    classifier.TryGetProperty("url", out var classifierUrl) ? classifierUrl.GetString() : null,
                    classifier.TryGetProperty("sha1", out var classifierSha1) ? classifierSha1.GetString() : null,
                    classifier.TryGetProperty("size", out var classifierSize) && classifierSize.TryGetInt64(out var classifierSizeValue)
                        ? classifierSizeValue
                        : 0);
            }
        }
    }

    /// <summary>
    /// 取这个库在当前系统下需要**解压**的 natives 包（没有则返回 <c>null</c>）。
    /// 与 <see cref="ResolveFiles"/> 的区别：那个是"要下载哪些文件"，这个是"哪些文件里的 dll/so 要解出来"。
    /// </summary>
    public static (string Path, string? Sha1, long Size)? ResolveNatives(JsonElement library)
    {
        if (!library.TryGetProperty("downloads", out var downloads) ||
            downloads.ValueKind != JsonValueKind.Object ||
            !downloads.TryGetProperty("classifiers", out var classifiers) ||
            classifiers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in NativesClassifierKeys(library))
        {
            if (classifiers.TryGetProperty(key, out var classifier) &&
                classifier.ValueKind == JsonValueKind.Object &&
                classifier.TryGetProperty("path", out var path) &&
                path.GetString() is { Length: > 0 } relative)
            {
                return (relative,
                    classifier.TryGetProperty("sha1", out var sha1) ? sha1.GetString() : null,
                    classifier.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue) ? sizeValue : 0);
            }
        }

        return null;
    }

    /// <summary>这个库在本机需要的 natives classifier 键：优先用 <c>natives</c> 映射，没有映射才退回通用命名。</summary>
    private static IEnumerable<string> NativesClassifierKeys(JsonElement library)
    {
        if (library.TryGetProperty("natives", out var natives) && natives.ValueKind == JsonValueKind.Object)
        {
            foreach (var osKey in NetCompat.IsWindows() ? new[] { "windows" }
                         : NetCompat.IsMacOS() ? new[] { "macos", "osx" }
                         : new[] { "linux" })
            {
                if (natives.TryGetProperty(osKey, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    value.GetString() is { Length: > 0 } classifier)
                {
                    yield return classifier;
                    yield break;
                }
            }

            // 有 natives 映射但当前系统没条目 ⇒ 这个库本机不需要 natives 包，别再退回通用键下错系统的包
            yield break;
        }

        foreach (var key in ClassifierKeys())
        {
            yield return key;
        }
    }

    private static IEnumerable<string> ClassifierKeys()
    {
        if (NetCompat.IsWindows())
        {
            yield return "natives-windows";
            yield return Environment.Is64BitProcess ? "natives-windows-64" : "natives-windows-32";
        }
        else if (NetCompat.IsMacOS())
        {
            yield return "natives-macos";
            yield return "natives-osx";
        }
        else
        {
            yield return "natives-linux";
        }
    }

    private static bool RuleMatches(JsonElement rule)
    {
        if (rule.TryGetProperty("os", out var os))
        {
            if (os.TryGetProperty("name", out var name) &&
                name.GetString() is { Length: > 0 } osName &&
                !string.Equals(osName, CurrentOsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 只处理 x86（32 位）这一种架构差异，其它一律视为匹配
            if (os.TryGetProperty("arch", out var arch) &&
                string.Equals(arch.GetString(), "x86", StringComparison.OrdinalIgnoreCase) &&
                Environment.Is64BitProcess)
            {
                return false;
            }
        }

        // 启动器不启用 demo / 自定义分辨率这些特性，要求它们的规则一律视为不匹配
        if (rule.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
        {
            foreach (var feature in features.EnumerateObject())
            {
                if (feature.Value.ValueKind == JsonValueKind.True)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
