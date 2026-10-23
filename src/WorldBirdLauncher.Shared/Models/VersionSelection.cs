namespace WorldBirdLauncher.Core.Models;

/// <summary>
/// 直连启动时的版本选择规则（2026-10-06 用户确认）：
/// <list type="bullet">
///   <item>服务器**不支持跨版本**（<see cref="ServerEntry.VersionRange"/> 为空）→ 严格遵循服务器声明的版本档；</item>
///   <item>服务器**支持跨版本**（有区间）→ 不自动挑，弹窗让用户挑。</item>
/// </list>
/// 这里只放纯逻辑（可单测），弹窗与安装流程在 App 层。
/// </summary>
public static class VersionSelection
{
    /// <summary>区间分隔符：波浪号 / 全角波浪号 / 短破折号 / 长破折号。</summary>
    private static readonly char[] RangeSeparators = ['~', '～', '–', '—'];

    /// <summary>
    /// 服务器是否支持跨版本：声明了区间字段（<c>versionRange</c>，如 "1.8.x ~ 1.19.x"），
    /// **或者版本文案本身就是区间**（如 "1.8 – 26.x"）。
    ///
    /// ★ 2026-10-08 修：编目里 19 台服务器（Hypixel / Wynncraft / CubeCraft / PikaNetwork …）
    /// 的区间只写在 <c>version</c> 文案里、<c>versionRange</c> 是空的，于是被当成"严格版本"服务器，
    /// 会拿文案里第一个版本号（"1.8"）当目标档 → 本机明明装着 1.18.2 却提示"本机还没有匹配的版本，
    /// 要不要下载 1.8"。现在这种也按跨版本处理（弹窗让用户从已装且兼容的版本里挑）。
    /// </summary>
    public static bool SupportsCrossVersion(ServerEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.VersionRange) || LooksLikeRange(entry.Version);

    /// <summary>版本文案是不是写成了区间（"1.8 – 26.x" / "1.8~1.20" / "1.8 - 1.21"）。</summary>
    public static bool LooksLikeRange(string? version)
    {
        var text = (version ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.IndexOfAny(RangeSeparators) >= 0)
        {
            return true;
        }

        // 连字符必须两侧都有空格才算区间，免得把 "1.20.1-生存" 这类后缀误判成区间
        return text.Contains(" - ", StringComparison.Ordinal);
    }

    /// <summary>
    /// 服务器声明的版本档：把 "1.7.10 (GTNH)" / "1.20.1 生存" 这类备注去掉，只留版本号。
    /// </summary>
    public static string TargetBand(ServerEntry entry)
    {
        var raw = (entry.Version ?? string.Empty).Trim();
        // 区间文案（"1.8 – 26.x"）取下界再比对，别把整串当版本号
        var rangeIndex = raw.IndexOfAny(RangeSeparators);
        if (rangeIndex < 0)
        {
            rangeIndex = raw.IndexOf(" - ", StringComparison.Ordinal);
        }

        if (rangeIndex > 0)
        {
            raw = raw[..rangeIndex].Trim();
        }

        var cut = raw.IndexOfAny(['(', '（', ' ', '\t']);
        var band = cut > 0 ? raw[..cut] : raw;
        return band.Trim();
    }

    /// <summary>
    /// 严格同档的已装版本（版本号从高到低）。用于"不支持跨版本"的服务器：
    /// 服务器写 1.7.10 就只认 1.7.10（含 1.7.10 光影 这类自定义目录名），写 1.20.x 就只认 1.20.* 这一档。
    /// </summary>
    public static IReadOnlyList<string> PickStrictCandidates(ServerEntry entry, IEnumerable<string> installedIds)
    {
        var band = TargetBand(entry);
        if (band.Length == 0)
        {
            return [];
        }

        return installedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => GroupOf(id) is not null)
            .Where(id => IsSameBand(band, id))
            .OrderByDescending(id => id, Comparer<string>.Create(CompareVersions))
            .ToList();
    }

    /// <summary>
    /// 与服务器兼容的已装版本（版本号从高到低）。用于"支持跨版本"的服务器弹窗让用户挑。
    /// 版本号解析不出来的自定义目录名（如"测试光影"）不会被选中，因为无法判断它到底属于哪个版本。
    /// </summary>
    public static IReadOnlyList<string> PickCompatibleCandidates(ServerEntry entry, IEnumerable<string> installedIds) =>
        installedIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => GroupOf(id) is not null)
            .Where(id => VersionCompatibility.IsCompatible(entry, id))
            .OrderByDescending(id => id, Comparer<string>.Create(CompareVersions))
            .ToList();

    /// <summary>版本号是不是落在服务器声明的这个档里。</summary>
    public static bool IsSameBand(string band, string versionId)
    {
        if (band.Length == 0 || versionId.Length == 0)
        {
            return false;
        }

        if (band.Contains('x') || band.Contains('X'))
        {
            return string.Equals(GroupOf(versionId), GroupOf(band), StringComparison.OrdinalIgnoreCase);
        }

        var leading = LeadingVersion(versionId);
        return leading.Length > 0 && string.Equals(leading, band, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>取版本号前缀（"1.7.10 (GTNH)" → "1.7.10"、"1.16.4-OptiFine_HD_U_G7" → "1.16.4"）。</summary>
    public static string LeadingVersion(string versionId)
    {
        var text = (versionId ?? string.Empty).TrimStart();
        var length = 0;
        while (length < text.Length && (NetCompat.IsAsciiDigit(text[length]) || text[length] == '.'))
        {
            length++;
        }

        return text[..length].TrimEnd('.');
    }

    /// <summary>版本号分组（1.21.4 → 1.21.x、26.1 → 26.x）；解析不出来返回 null。</summary>
    public static string? GroupOf(string versionId) =>
        VersionCompatibility.GroupOf(LeadingVersion(versionId));

    /// <summary>
    /// 从远程正式版清单里挑出这个档最合适的具体版本：优先同档里版本号最高的正式版
    /// （例如档 "1.20.x" → "1.20.6"；档 "1.7.10" → "1.7.10"）。
    /// </summary>
    public static string? PickRecommendedRemoteVersion(string band, IEnumerable<string> remoteReleaseIds)
    {
        if (string.IsNullOrWhiteSpace(band))
        {
            return null;
        }

        var candidates = remoteReleaseIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => GroupOf(id) is not null)
            .Where(id => IsSameBand(band, id))
            .OrderByDescending(id => id, Comparer<string>.Create(CompareVersions))
            .ToList();
        return candidates.FirstOrDefault();
    }

    /// <summary>版本号比较（忽略非数字后缀；解析不出来的排最后）。</summary>
    public static int CompareVersions(string? left, string? right)
    {
        var a = ParseNumbers(left);
        var b = ParseNumbers(right);
        if (a is null && b is null)
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        for (var i = 0; i < 3; i++)
        {
            var diff = a[i].CompareTo(b[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return 0;
    }

    private static int[]? ParseNumbers(string? versionId)
    {
        var leading = LeadingVersion(versionId ?? string.Empty);
        if (leading.Length == 0)
        {
            return null;
        }

        var parts = leading.Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3 && i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }
}
