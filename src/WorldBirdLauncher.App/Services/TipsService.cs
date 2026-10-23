using WorldBirdLauncher.Core.Services;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 启动页底部的游戏提示（10 秒轮播）。
///
/// 文案优先用服务端下发的（<c>GET /api/v1/tips</c> → 缓存到
/// <c>%APPDATA%\WorldBirdLauncher\catalog\tips.json</c>），拉不到就退回程序内置的那份。
/// </summary>
public sealed class TipsService
{
    private static readonly string TipsCachePath = Path.Combine(AppSettings.Directory, "catalog", "tips.json");

    private List<string> _tips = [];

    public TipsService() => Reload();

    public IReadOnlyList<string> Tips => _tips.Count > 0 ? _tips : FallbackTips.Value;

    /// <summary>服务端下发后重新读取缓存（拉不到就用内置那份）。</summary>
    public void Reload()
    {
        try
        {
            if (File.Exists(TipsCachePath))
            {
                var cached = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TipsCachePath));
                _tips = cached?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"读取提示文案缓存失败：{ex.Message}");
            _tips = [];
        }
    }

    public string Next()
    {
        var tips = Tips;
        return tips.Count == 0 ? "（暂无提示）" : tips[NetCompat.NextRandom(tips.Count)];
    }

    private static readonly Lazy<IReadOnlyList<string>> FallbackTips = new(() =>
    {
        try
        {
            return new TipsCatalog().Load();
        }
        catch (Exception)
        {
            return [];
        }
    });
}
