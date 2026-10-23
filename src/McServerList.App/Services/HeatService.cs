using System.Text.Json;

namespace McServerList.App.Services;

/// <summary>
/// 服务器热度：按用户行为打分并做时间衰减，热度靠前的自动进入启动页热门榜。
///
/// 模型（可在下方常量里调整）：
///   直连启动 +0.1 / 次、查看详情 +0.05 / 次、当前已收藏 +2.0；
///   每次事件按半衰期 30 天衰减：贡献 = 权重 × 0.5^(距今天数 / 30)。
/// 事件落盘在 %APPDATA%\McServerList\heat.json。
/// </summary>
public sealed class HeatService
{
    private const double LaunchWeight = 0.1;
    private const double ViewWeight = 0.05;
    private const double FavoriteWeight = 2.0;
    private const double HalfLifeDays = 30;
    private const int MaxEventsPerServer = 200;

    private readonly Dictionary<string, List<HeatEvent>> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public static HeatService Current { get; } = new();

    private static string FilePath => Path.Combine(AppSettings.Directory, "heat.json");

    private HeatService() => Load();

    public void RecordLaunch(string serverId) => Record(serverId, "launch");

    public void RecordView(string serverId) => Record(serverId, "view");

    /// <summary>计算某个服务器的热度（含时间衰减）；收藏为持续状态，不计衰减。</summary>
    public double Score(string serverId, bool isFavorite)
    {
        var score = isFavorite ? FavoriteWeight : 0;
        lock (_gate)
        {
            if (!_events.TryGetValue(serverId, out var list))
            {
                return score;
            }

            var now = DateTimeOffset.Now;
            foreach (var item in list)
            {
                var weight = item.Type == "launch" ? LaunchWeight : ViewWeight;
                var days = Math.Max(0, (now - item.Time).TotalDays);
                score += weight * Math.Pow(0.5, days / HalfLifeDays);
            }
        }

        return score;
    }

    /// <summary>按热度排序的服务器 id（只含有点击/收藏记录的服务器）。</summary>
    public IReadOnlyList<(string Id, double Score)> Ranking(IReadOnlyCollection<string> favoriteIds, int top = 50)
    {
        var favorites = favoriteIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            return _events.Keys
                .Select(id => (Id: id, Score: Score(id, favorites.Contains(id))))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(top)
                .ToList();
        }
    }

    private void Record(string serverId, string type)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return;
        }

        lock (_gate)
        {
            if (!_events.TryGetValue(serverId, out var list))
            {
                list = [];
                _events[serverId] = list;
            }

            list.Add(new HeatEvent(type, DateTimeOffset.Now));
            if (list.Count > MaxEventsPerServer)
            {
                list.RemoveRange(0, list.Count - MaxEventsPerServer); // 只保留最近的事件
            }
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, List<HeatEvent>>>(
                File.ReadAllText(FilePath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data is null)
            {
                return;
            }

            foreach (var (id, list) in data)
            {
                _events[id] = list;
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"热度数据读取失败：{ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<string, List<HeatEvent>> snapshot;
            lock (_gate)
            {
                snapshot = _events.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            }

            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.LogLine($"热度数据保存失败：{ex.Message}");
        }
    }

    private sealed record HeatEvent(string Type, DateTimeOffset Time);
}
