using System.Text.Json.Serialization;

namespace McServerList.App.Ads;

/// <summary>
/// 一条推广卡（服务端 <c>GET /api/v1/ads</c> 下发；字段对齐需求文档 5.2.2）。
/// 广告**只从广告接口来**，不进入自然列表、不参与排序与筛选。
/// </summary>
public sealed class AdItem
{
    public string AdId { get; set; } = string.Empty;

    public long CampaignId { get; set; }

    public string ServerId { get; set; } = string.Empty;

    /// <summary>广告位 code（home_hot_top / list_after_4 / list_after_20 / list_after_40 / detail_bottom）。</summary>
    public string Slot { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? IconUrl { get; set; }

    public string? TargetUrl { get; set; }

    public string? ServerIp { get; set; }

    public int? ServerPort { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = [];

    [JsonIgnore]
    public bool IsAd => true;
}

/// <summary>本地广告缓存（含拉取时间；5～10 分钟内不重复请求，失败时继续用缓存）。</summary>
public sealed class AdCache
{
    public DateTimeOffset FetchedAt { get; set; }

    public List<AdItem> Items { get; set; } = [];
}

/// <summary>「不感兴趣」记录：本地隐藏 7 天。</summary>
public sealed class HiddenAd
{
    public long CampaignId { get; set; }

    public DateTimeOffset Until { get; set; }
}

/// <summary>待上报的广告事件（断网时进本地队列，下次启动重试）。</summary>
public sealed class QueuedAdEvent
{
    public string? AdId { get; set; }

    public long CampaignId { get; set; }

    public string Slot { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string DeviceId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public long Timestamp { get; set; }

    public string Nonce { get; set; } = string.Empty;

    public string Sign { get; set; } = string.Empty;
}
