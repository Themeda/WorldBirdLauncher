namespace McServerList.Core.Models;

/// <summary>
/// 判断"即将启动的游戏版本"与目标服务器支持的版本是否匹配（跨版本服务器按区间判断）。
/// </summary>
public static class VersionCompatibility
{
    /// <summary>返回服务器支持的版本描述（优先区间），用于提示文案。</summary>
    public static string DescribeSupported(ServerEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.VersionRange)
            ? entry.VersionRange!
            : string.IsNullOrWhiteSpace(entry.Version) ? "未知" : entry.Version;

    /// <summary>游戏版本是否落在服务器支持的范围内。</summary>
    public static bool IsCompatible(ServerEntry entry, string? gameVersionId)
    {
        if (string.IsNullOrWhiteSpace(gameVersionId))
        {
            return true; // 没选版本时不做限制
        }

        if (!TryParseKey(gameVersionId, out var game))
        {
            return true; // 版本号解析不出来（如自定义版本名）就不拦
        }

        // 跨版本服务器：按区间判断
        if (!string.IsNullOrWhiteSpace(entry.VersionRange))
        {
            var parts = NetCompat.SplitTrim(entry.VersionRange!, '~');
            // 只比到小版本（1.19.x 应包含 1.19.2；26.1 就是 26.1 这一档）
            if (parts.Length == 2 && TryParseKey(parts[0], out var low) && TryParseKey(parts[1], out var high))
            {
                return game >= low && game <= high;
            }
        }

        // 普通服务器：看版本分组是否命中
        var group = GroupOf(gameVersionId);
        return group is null || entry.VersionGroups.Count == 0 || entry.VersionGroups.Contains(group);
    }

    /// <summary>把游戏版本号映射到筛选分组（如 1.21.4 → 1.21.x、26.1 → 26.x）。</summary>
    public static string? GroupOf(string? versionId)
    {
        var cleaned = (versionId ?? string.Empty).Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }

        if (!TryParseKey(cleaned, out var value))
        {
            return null;
        }

        if (value >= 20)
        {
            return $"{(int)value}.x";
        }

        var parts = cleaned.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}.x" : null;
    }

    /// <summary>
    /// 这个游戏版本能不能用现代 MC 的 <c>--quickPlayMultiplayer</c> 直连参数。
    ///
    /// 该参数是 **1.20 才有**的：更老的版本（1.19.4 / 1.12.2 / 1.7.10…）不认它，
    /// 会**静默忽略**（表现为"点了启动但没进服"），那些版本必须用老的 <c>--server/--port</c>。
    /// 版本号解析不出来（自定义版本名）时按"新版"处理，保持原来行为。
    /// </summary>
    public static bool SupportsQuickPlay(string? versionId) =>
        !TryParseKey(versionId, out var value) || value >= 1.20;

    /// <summary>
    /// 解析成"主版本.小版本"的比较键（忽略补丁号）：1.21.4 → 1.21、1.19.2 → 1.19、26.1 → 26.01。
    /// 这样区间 1.8.x ~ 1.19.x 才能正确涵盖 1.19.2。
    /// </summary>
    public static bool TryParseVersionKey(string? text, out double value) => TryParseKey(text, out value);

    private static bool TryParseKey(string? text, out double value)
    {
        value = 0;
        var cleaned = NetCompat.ReplaceIgnoreCase((text ?? string.Empty).Trim(), ".x", string.Empty);
        if (cleaned.Length == 0)
        {
            return false;
        }

        var parts = cleaned.Split('.', '-', '+');
        if (!int.TryParse(parts[0], out var major))
        {
            return false;
        }

        var minor = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
        value = major + minor / 100d; // 26.1 → 26.01、1.21.4 → 1.21
        return true;
    }
}
