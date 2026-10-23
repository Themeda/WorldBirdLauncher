using System.Net;
using System.Net.Sockets;
using System.Text;
using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.Core.Protocol;

/// <summary>
/// Minecraft Query 协议（GameSpy4，UDP）：可获取地图、游戏模式、插件列表与完整在线玩家名单。
/// 需要服务端在 server.properties 中开启 <c>enable-query=true</c>，未开启时查询会失败，
/// 属于"尽力而为"的增强信息，不作为列表展示的必要条件。
/// </summary>
public sealed class QueryProtocol
{
    private const byte TypeHandshake = 0x09;
    private const byte TypeStat = 0x00;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public async Task<ServerStatus> QueryAsync(
        string host,
        int port = 25565,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        NetCompat.ThrowIfNullOrWhiteSpace(host, nameof(host));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);
        var token = timeoutSource.Token;

        try
        {
            using var client = new UdpClient();
            client.Connect(host, port);

            var challenge = await RequestChallengeAsync(client, token).ConfigureAwait(false);
            var payload = await RequestFullStatAsync(client, challenge, token).ConfigureAwait(false);

            return ParseFullStat(payload);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ServerStatus.Offline("Query 查询超时，服务器可能未开启 Query。", StatusSource.Query);
        }
        catch (Exception ex)
        {
            return ServerStatus.Offline($"Query 查询失败：{ex.Message}", StatusSource.Query);
        }
    }

    private static async Task<int> RequestChallengeAsync(UdpClient client, CancellationToken token)
    {
        var sessionId = NetCompat.NextRandom();
        var request = new byte[7];
        request[0] = 0xFE;
        request[1] = 0xFD;
        request[2] = TypeHandshake;
        WriteInt32(request, 3, sessionId);

        await NetCompat.WithCancellation(client.SendAsync(request, request.Length), token).ConfigureAwait(false);
        var response = await ReceiveAsync(client, token).ConfigureAwait(false);

        if (response.Length < 5 || response[0] != TypeHandshake)
        {
            throw new InvalidDataException("Query 握手响应格式异常。");
        }

        var challengeText = ReadNullTerminatedString(response, 5, out _).Trim();
        if (!int.TryParse(challengeText, out var challenge))
        {
            throw new InvalidDataException($"无法解析 Query challenge：{challengeText}。");
        }

        return challenge;
    }

    private static async Task<byte[]> RequestFullStatAsync(UdpClient client, int challenge, CancellationToken token)
    {
        var sessionId = NetCompat.NextRandom();
        var request = new byte[15];
        request[0] = 0xFE;
        request[1] = 0xFD;
        request[2] = TypeStat;
        WriteInt32(request, 3, sessionId);
        WriteInt32(request, 7, challenge);
        // 最后 4 字节保持为 0（协议要求）

        await NetCompat.WithCancellation(client.SendAsync(request, request.Length), token).ConfigureAwait(false);
        var response = await ReceiveAsync(client, token).ConfigureAwait(false);

        if (response.Length < 16 || response[0] != TypeStat)
        {
            throw new InvalidDataException("Query 完整状态响应格式异常。");
        }

        return response;
    }

    private static async Task<byte[]> ReceiveAsync(UdpClient client, CancellationToken token)
    {
        var result = await NetCompat.WithCancellation(client.ReceiveAsync(), token).ConfigureAwait(false);
        return result.Buffer;
    }

    private static ServerStatus ParseFullStat(byte[] data)
    {
        // 结构：[0]=类型 [1..4]=SessionID [5..15]=11 字节固定填充，
        // 之后是若干 "键\0值\0" 键值对，以空键结束；随后 10 字节填充，
        // 再后是 "player_\0\0" 与玩家名列表（各自 \0 结尾），最后以空名结束。
        var index = 5 + 11;
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (index < data.Length)
        {
            var key = ReadNullTerminatedString(data, index, out index);
            if (key.Length == 0)
            {
                break;
            }

            var value = ReadNullTerminatedString(data, index, out index);
            fields[key] = value;
        }

        index = Math.Min(index + 10, data.Length);

        // 玩家列表：先有 "player_" 头，随后以空字符串分隔，再依次是玩家名。
        // 不同服务端的空串数量略有差异，这里统一跳过所有空字符串。
        var players = new List<string>();
        if (index < data.Length)
        {
            var header = ReadNullTerminatedString(data, index, out index);
            if (header.Length > 0 && !header.StartsWith("player_", StringComparison.Ordinal))
            {
                players.Add(header);
            }

            while (index < data.Length)
            {
                var player = ReadNullTerminatedString(data, index, out index);
                if (!string.IsNullOrEmpty(player))
                {
                    players.Add(player);
                }
            }
        }

        int? ParseInt(string key) =>
            fields.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : null;

        return new ServerStatus
        {
            IsOnline = true,
            OnlinePlayers = ParseInt("numplayers"),
            MaxPlayers = ParseInt("maxplayers"),
            VersionName = fields.TryGetValue("version", out var version) ? version : null,
            HostName = fields.TryGetValue("hostname", out var hostname) ? MinecraftText.Normalize(hostname) : null,
            // 同样保留 § 颜色代码与换行（Query 的 motd 是原始字符串）
            Motd = fields.TryGetValue("motd", out var motd) && !string.IsNullOrWhiteSpace(motd)
                ? MinecraftText.KeepFormatting(motd).Trim()
                : null,
            Map = fields.TryGetValue("map", out var map) ? map : null,
            GameType = fields.TryGetValue("gametype", out var gameType) ? gameType : null,
            Plugins = fields.TryGetValue("plugins", out var plugins) ? plugins : null,
            Players = players,
            Source = StatusSource.Query,
        };
    }

    private static string ReadNullTerminatedString(byte[] data, int start, out int next)
    {
        var end = start;
        while (end < data.Length && data[end] != 0)
        {
            end++;
        }

        next = Math.Min(end + 1, data.Length);
        return Encoding.UTF8.GetString(data, start, end - start);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
