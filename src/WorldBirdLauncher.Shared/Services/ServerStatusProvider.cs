using WorldBirdLauncher.Core.Models;
using WorldBirdLauncher.Core.Networking;
using WorldBirdLauncher.Core.Protocol;

namespace WorldBirdLauncher.Core.Services;

/// <summary>
/// 默认状态查询实现：先做 Server List Ping，再尝试 Query 补充详细数据。
/// Query 失败不影响整体结果（大多数服务器未开启 Query）。
/// </summary>
public sealed class ServerStatusProvider : IServerStatusProvider
{
    private readonly ServerListPing _ping;
    private readonly QueryProtocol _query;
    private readonly MinecraftSrvResolver _resolver;
    private readonly bool _enableQuery;
    private readonly TimeSpan _timeout;

    public ServerStatusProvider(
        ServerListPing? ping = null,
        QueryProtocol? query = null,
        MinecraftSrvResolver? resolver = null,
        bool enableQuery = true,
        TimeSpan? timeout = null)
    {
        _ping = ping ?? new ServerListPing();
        _query = query ?? new QueryProtocol();
        _resolver = resolver ?? new MinecraftSrvResolver();
        _enableQuery = enableQuery;
        _timeout = timeout ?? ServerListPing.DefaultTimeout;
    }

    public async Task<ServerStatus> QueryAsync(ServerEntry entry, CancellationToken cancellationToken = default)
    {
        NetCompat.ThrowIfNull(entry, nameof(entry));

        var (host, port) = await _resolver
            .ResolveAsync(entry.Address, entry.Port, cancellationToken)
            .ConfigureAwait(false);

        var pingTask = _ping.QueryAsync(host, port, _timeout, cancellationToken);
        var queryTask = _enableQuery
            ? _query.QueryAsync(host, entry.QueryPort ?? port, _timeout, cancellationToken)
            : Task.FromResult(ServerStatus.Offline("未启用 Query 查询。", StatusSource.Query));

        var results = await Task.WhenAll(pingTask, queryTask).ConfigureAwait(false);
        var ping = results[0];
        var query = results[1];

        if (!ping.IsOnline)
        {
            return query.IsOnline ? query : ping;
        }

        if (!query.IsOnline)
        {
            return ping;
        }

        return ping with
        {
            OnlinePlayers = query.OnlinePlayers ?? ping.OnlinePlayers,
            MaxPlayers = query.MaxPlayers ?? ping.MaxPlayers,
            VersionName = query.VersionName ?? ping.VersionName,
            HostName = query.HostName,
            Motd = query.Motd ?? ping.Motd,
            Map = query.Map,
            GameType = query.GameType,
            Plugins = query.Plugins,
            Players = query.Players.Count > 0 ? query.Players : ping.Players,
            Source = StatusSource.Ping | StatusSource.Query,
        };
    }
}
