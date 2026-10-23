using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McServerList.App.Services;

namespace McServerList.App.Ads;

/// <summary>
/// 客户端广告服务（需求文档 5.2）：
/// ① 一次批量拉全部广告位（超时 3 秒，失败静默用缓存，无缓存就不显示，绝不影响自然列表）；
/// ② 事件上报（曝光 / 点击详情 / 复制 IP / 进服），失败进本地队列、下次启动重试；
/// ③ 「不感兴趣」本地隐藏 7 天。
///
/// 只连目录服务端（<see cref="AppInfo.CatalogEndpoint"/>），走 <c>X-Client-Key</c> 轻校验。
/// </summary>
public sealed class AdService
{
    /// <summary>需求文档要求"广告缓存 ≥ 5 分钟"，这里取 8 分钟。</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(8);

    private static readonly TimeSpan HideLifetime = TimeSpan.FromDays(7);

    private static readonly string[] Slots =
    [
        "home_hot_top", "list_after_4", "list_after_20", "list_after_40", "detail_bottom",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly string _hiddenPath;
    private readonly string _queuePath;
    private readonly string _deviceId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _fileGate = new();
    private AdCache _cache = new();
    private List<HiddenAd> _hidden = [];
    private List<QueuedAdEvent> _queue = [];
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    /// <summary>会话 id：本次运行随机生成，用于服务端做"停留时间"类的后续统计。</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public AdService(string? deviceId = null)
    {
        var dir = AppSettings.Directory;
        System.IO.Directory.CreateDirectory(dir);
        _cachePath = System.IO.Path.Combine(dir, "ads-cache.json");
        _hiddenPath = System.IO.Path.Combine(dir, "ads-hidden.json");
        _queuePath = System.IO.Path.Combine(dir, "ads-queue.json");
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? "unknown" : deviceId!;
        _http = new HttpClient
        {
            BaseAddress = new Uri(AppInfo.CatalogEndpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(3), // 非功能需求：广告接口超时 ≤ 3 秒
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("McServerListLauncher/0.2");
        _http.DefaultRequestHeaders.Add("X-Client-Key", AppInfo.CatalogClientKey);
        LoadLocal();
    }

    /// <summary>当前可用的广告（已过滤「不感兴趣」与已过期缓存）。</summary>
    public IReadOnlyList<AdItem> Items
    {
        get
        {
            lock (_fileGate)
            {
                var now = DateTimeOffset.Now;
                return _cache.Items
                    .Where(item => !_hidden.Any(h => h.CampaignId == item.CampaignId && h.Until > now))
                    .ToList();
            }
        }
    }

    public bool HasFreshCache =>
        DateTimeOffset.Now - _cache.FetchedAt < CacheLifetime && _cache.Items.Count > 0;

    /// <summary>按广告位取广告（最多 <paramref name="count"/> 条）。</summary>
    public IReadOnlyList<AdItem> ForSlot(string slot, int count = 1) =>
        Items.Where(i => string.Equals(i.Slot, slot, StringComparison.OrdinalIgnoreCase)).Take(count).ToList();

    /// <summary>
    /// 拉一次广告。失败时保留缓存（返回 false）；同一分钟内不重复请求。
    /// <paramref name="force"/> = 用户手动刷新时忽略节流。
    /// </summary>
    public async Task<bool> RefreshAsync(bool force = false, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!force && HasFreshCache)
            {
                return false;
            }

            if (!force && DateTimeOffset.Now - _lastAttempt < TimeSpan.FromMinutes(1))
            {
                return false;
            }

            _lastAttempt = DateTimeOffset.Now;
            var url = $"api/v1/ads?slots={Uri.EscapeDataString(string.Join(",", Slots))}&limit=1";
            using var response = await _http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var items = data.Deserialize<List<AdItem>>(Json) ?? [];
            lock (_fileGate)
            {
                _cache = new AdCache { FetchedAt = DateTimeOffset.Now, Items = items };
            }

            SaveFile(_cachePath, _cache);
            return true;
        }
        catch (Exception ex)
        {
            App.LogLine($"拉取广告失败（用缓存降级）：{ex.Message}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>标记「不感兴趣」：本地隐藏 7 天并静默上报一次。</summary>
    public void Hide(AdItem item)
    {
        lock (_fileGate)
        {
            _hidden.RemoveAll(h => h.CampaignId == item.CampaignId);
            _hidden.Add(new HiddenAd { CampaignId = item.CampaignId, Until = DateTimeOffset.Now.Add(HideLifetime) });
        }

        SaveFile(_hiddenPath, _hidden);
        Report(item, "click_detail"); // 文档里没有"忽略"类型，用一次点击类事件让服主知道被忽略过（服务端 24h 去重）
    }

    /// <summary>上报一个事件（曝光 / 点击详情 / 复制 IP / 进服），失败进队列下次启动重试。</summary>
    public void Report(AdItem item, string eventType)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var request = new QueuedAdEvent
        {
            AdId = item.AdId,
            CampaignId = item.CampaignId,
            Slot = item.Slot,
            EventType = eventType,
            DeviceId = _deviceId,
            SessionId = SessionId,
            Timestamp = timestamp,
            Nonce = nonce,
            Sign = Sign(item.CampaignId, item.Slot, eventType, timestamp, nonce),
        };
        lock (_fileGate)
        {
            _queue.Add(request);
        }

        SaveFile(_queuePath, _queue);
        _ = FlushQueueAsync();
    }

    /// <summary>把本地队列里的上报全发一遍（启动时调一次；失败的原样留着）。</summary>
    public async Task FlushQueueAsync(CancellationToken token = default)
    {
        List<QueuedAdEvent> pending;
        lock (_fileGate)
        {
            pending = _queue.ToList();
        }

        if (pending.Count == 0)
        {
            return;
        }

        var remaining = new List<QueuedAdEvent>();
        foreach (var item in pending)
        {
            if (token.IsCancellationRequested)
            {
                remaining.Add(item);
                continue;
            }

            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(item, Json), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync("api/v1/ads/event", content, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && (int)response.StatusCode >= 500)
                {
                    remaining.Add(item); // 服务端故障：留着下次重试；4xx（含去重 202）算成功
                }
            }
            catch (Exception)
            {
                remaining.Add(item);
            }
        }

        lock (_fileGate)
        {
            _queue = remaining;
        }

        SaveFile(_queuePath, _queue);
    }

    private string Sign(long campaignId, string slot, string eventType, long timestamp, string nonce)
    {
        var canonical = $"{campaignId}|{slot}|{eventType}|{_deviceId}|{timestamp}|{nonce}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppInfo.CatalogClientKey));
        // net48 没有 Convert.ToHexString → 统一走 NetCompat（AGENTS 坑 34）
        return NetCompat.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)), lowercase: true);
    }

    private void LoadLocal()
    {
        _cache = LoadFile<AdCache>(_cachePath) ?? new AdCache();
        _hidden = LoadFile<List<HiddenAd>>(_hiddenPath) ?? [];
        _queue = LoadFile<List<QueuedAdEvent>>(_queuePath) ?? [];
        _hidden.RemoveAll(h => h.Until <= DateTimeOffset.Now);
    }

    private static T? LoadFile<T>(string path)
    {
        try
        {
            return System.IO.File.Exists(path)
                ? JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path), Json)
                : default;
        }
        catch (Exception ex)
        {
            App.LogLine($"读取 {System.IO.Path.GetFileName(path)} 失败：{ex.Message}");
            return default;
        }
    }

    private static void SaveFile<T>(string path, T value)
    {
        try
        {
            System.IO.File.WriteAllText(path, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            App.LogLine($"写入 {System.IO.Path.GetFileName(path)} 失败：{ex.Message}");
        }
    }
}
