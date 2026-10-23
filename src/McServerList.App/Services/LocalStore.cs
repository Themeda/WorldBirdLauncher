using System.Text.Json;

namespace McServerList.App.Services;

/// <summary>收藏与连接历史的本地存储（JSON 分文件）。</summary>
public sealed class LocalStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<HistoryRecord> _history = [];
    private readonly Dictionary<string, string> _preferredRoutes = new(StringComparer.OrdinalIgnoreCase);

    private static string FavoritesPath => Path.Combine(AppSettings.Directory, "favorites.json");

    private static string HistoryPath => Path.Combine(AppSettings.Directory, "history.json");

    private static string RoutesPath => Path.Combine(AppSettings.Directory, "routes.json");

    public IReadOnlyCollection<string> Favorites => _favorites;

    public IReadOnlyList<HistoryRecord> History => _history;

    public static LocalStore Load()
    {
        var store = new LocalStore();
        try
        {
            if (File.Exists(FavoritesPath))
            {
                foreach (var id in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavoritesPath), Options) ?? [])
                {
                    store._favorites.Add(id);
                }
            }

            if (File.Exists(HistoryPath))
            {
                store._history.AddRange(
                    JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(HistoryPath), Options) ?? []);
            }

            if (File.Exists(RoutesPath))
            {
                foreach (var (id, endpoint) in
                         JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RoutesPath), Options) ?? [])
                {
                    store._preferredRoutes[id] = endpoint;
                }
            }
        }
        catch (Exception)
        {
            // 数据损坏时从空开始
        }

        return store;
    }

    public bool IsFavorite(string id) => _favorites.Contains(id);

    /// <summary>该服务器选择的直连线路（多线路时由用户在详情页勾选）。</summary>
    public string? GetPreferredRoute(string serverId) =>
        _preferredRoutes.TryGetValue(serverId, out var endpoint) ? endpoint : null;

    public void SetPreferredRoute(string serverId, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _preferredRoutes.Remove(serverId);
        }
        else
        {
            _preferredRoutes[serverId] = endpoint!;
        }

        Save();
    }

    public bool ToggleFavorite(string id)
    {
        var added = _favorites.Add(id);
        if (!added)
        {
            _favorites.Remove(id);
        }

        Save();
        return added;
    }

    public void AddHistory(string serverId, string serverName, string address)
    {
        _history.Insert(0, new HistoryRecord
        {
            ServerId = serverId,
            ServerName = serverName,
            Address = address,
            Time = DateTimeOffset.Now,
        });

        const int maxRecords = 50;
        if (_history.Count > maxRecords)
        {
            _history.RemoveRange(maxRecords, _history.Count - maxRecords);
        }

        Save();
    }

    /// <summary>用户手动移除一条"已下架但保留"的服务器：取消收藏并清掉游玩历史（否则下次还会被保留）。</summary>
    public void Forget(string serverId)
    {
        _favorites.Remove(serverId);
        _history.RemoveAll(h => string.Equals(h.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    /// <summary>只清掉某台服务器的游玩历史（不动收藏）——「最近玩过的服务器」里点「移除所选」用。</summary>
    public void RemoveHistory(string serverId)
    {
        _history.RemoveAll(h => string.Equals(h.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(FavoritesPath, JsonSerializer.Serialize(_favorites.ToList(), Options));
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(_history, Options));
            File.WriteAllText(RoutesPath, JsonSerializer.Serialize(_preferredRoutes, Options));
        }
        catch (Exception)
        {
            // 写入失败不影响主流程
        }
    }
}

/// <summary>一条连接历史。</summary>
public sealed class HistoryRecord
{
    public string ServerId { get; set; } = string.Empty;

    public string ServerName { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public DateTimeOffset Time { get; set; }
}
