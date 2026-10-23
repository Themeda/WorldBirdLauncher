using DnsClient;
using DnsClient.Protocol;

namespace WorldBirdLauncher.Core.Networking;

/// <summary>
/// Minecraft Java 版服务器地址解析：当未显式指定端口时，优先查询
/// <c>_minecraft._tcp.&lt;域名&gt;</c> 的 SRV 记录，查不到则回退为直连。
/// 大量服务器（尤其是 <c>play.xxx</c> 形式的域名）只有 SRV 记录才能正确连上。
/// </summary>
public sealed class MinecraftSrvResolver
{
    private readonly LookupClient _client;
    private readonly Dictionary<string, (string Host, int Port)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public MinecraftSrvResolver(LookupClient? client = null)
    {
        _client = client ?? new LookupClient(new LookupClientOptions
        {
            Timeout = TimeSpan.FromSeconds(3),
            Retries = 1,
            UseCache = true,
        });
    }

    public async Task<(string Host, int Port)> ResolveAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (port != 25565 || string.IsNullOrWhiteSpace(host) || IsIpAddress(host))
        {
            return (host, port);
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(host, out var cached))
            {
                return cached;
            }
        }

        var resolved = (host, port);
        try
        {
            var response = await _client
                .QueryAsync($"_minecraft._tcp.{host}", QueryType.SRV, QueryClass.IN, cancellationToken)
                .ConfigureAwait(false);

            var record = response.Answers.SrvRecords()
                .OrderBy(r => r.Priority)
                .ThenByDescending(r => r.Weight)
                .FirstOrDefault();

            if (record is not null && !string.IsNullOrWhiteSpace(record.Target.Value))
            {
                resolved = (record.Target.Value.TrimEnd('.'), record.Port);
            }
        }
        catch (Exception)
        {
            // 无 SRV 记录（或 DNS 不可用）时按直连处理
        }

        lock (_gate)
        {
            _cache[host] = resolved;
        }

        return resolved;
    }

    private static bool IsIpAddress(string host) =>
        System.Net.IPAddress.TryParse(host, out _);
}
