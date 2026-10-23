namespace McServerList.Core.Models;

/// <summary>服务器状态数据来源。</summary>
[Flags]
public enum StatusSource
{
    None = 0,

    /// <summary>Server List Ping（TCP，必做，绝大多数服务器可用）。</summary>
    Ping = 1,

    /// <summary>Query 协议（UDP，服务端需开启 enable-query 才可用）。</summary>
    Query = 2,
}

/// <summary>一次服务器状态查询的结果。</summary>
public sealed record ServerStatus
{
    /// <summary>是否成功获取到状态。</summary>
    public bool IsOnline { get; init; }

    /// <summary>在线玩家数。</summary>
    public int? OnlinePlayers { get; init; }

    /// <summary>玩家上限。</summary>
    public int? MaxPlayers { get; init; }

    /// <summary>网络延迟（毫秒）。</summary>
    public long? LatencyMs { get; init; }

    /// <summary>服务器实际版本名，如 "Paper 1.20.4"。</summary>
    public string? VersionName { get; init; }

    /// <summary>服务器名称（Query 的 hostname，通常比展示名更准确）。</summary>
    public string? HostName { get; init; }

    /// <summary>MOTD（已去除颜色代码）。</summary>
    public string? Motd { get; init; }

    /// <summary>服务器图标，data URI 形式（Ping 返回的 favicon）。</summary>
    public string? FaviconDataUri { get; init; }

    /// <summary>在线玩家名单（仅 Query 可用，或 Ping 的 players.sample）。</summary>
    public IReadOnlyList<string> Players { get; init; } = Array.Empty<string>();

    /// <summary>地图名称（Query）。</summary>
    public string? Map { get; init; }

    /// <summary>游戏模式描述（Query）。</summary>
    public string? GameType { get; init; }

    /// <summary>已安装插件列表（Query）。</summary>
    public string? Plugins { get; init; }

    /// <summary>查询结果来源。</summary>
    public StatusSource Source { get; init; } = StatusSource.None;

    /// <summary>失败原因（用户可读）。</summary>
    public string? Error { get; init; }

    /// <summary>查询完成时间。</summary>
    public DateTimeOffset QueriedAt { get; init; } = DateTimeOffset.Now;

    public static ServerStatus Offline(string reason, StatusSource source = StatusSource.None) =>
        new() { IsOnline = false, Error = reason, Source = source };
}
