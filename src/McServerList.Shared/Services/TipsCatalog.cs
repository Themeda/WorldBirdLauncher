using System.Text.Json;

namespace McServerList.Core.Services;

/// <summary>
/// 内置游戏提示（<c>Data/tips.json</c>，随 Shared 程序集内嵌）。
///
/// 客户端优先用服务端下发的文案（缓存到本地），拉不到就退回这份内置；
/// 服务端 <c>GET /api/v1/tips</c> 也是直接返回这一份。
/// </summary>
public sealed class TipsCatalog
{
    private const string ResourceSuffix = ".Data.tips.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<string> Load()
    {
        var name = typeof(TipsCatalog).Assembly.GetManifestResourceNames()
                       .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                   ?? throw new InvalidOperationException($"未找到内嵌资源（*{ResourceSuffix}）");

        using var stream = typeof(TipsCatalog).Assembly.GetManifestResourceStream(name)!;
        var tips = JsonSerializer.Deserialize<List<string>>(stream, Options) ?? [];
        return tips.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }
}
