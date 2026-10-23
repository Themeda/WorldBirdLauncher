using System.Diagnostics;
using System.Text;
using System.Text.Json;
using McServerList.Core.Models;

namespace McServerList.Core.Protocol;

/// <summary>
/// Server List Ping（现代协议）：对服务器 TCP 端口发起握手 + 状态请求，
/// 获取在线人数、玩法版本、MOTD、图标与延迟。绝大多数 Java 版服务器可用。
/// </summary>
public sealed class ServerListPing
{
    /// <summary>状态查询时使用的协议号（1.8，服务端普遍忽略该值）。</summary>
    private const int HandshakeProtocolVersion = 47;

    /// <summary>握手包 ID。</summary>
    private const int PacketIdHandshake = 0x00;

    /// <summary>状态请求包 ID。</summary>
    private const int PacketIdStatusRequest = 0x00;

    /// <summary>握手后的连接状态：1 = 状态查询。</summary>
    private const int NextStateStatus = 1;

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

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            await NetCompat.WithCancellation(client.ConnectAsync(host, port), token).ConfigureAwait(false);
            var stream = client.GetStream();

            await SendHandshakeAsync(stream, host, port, token).ConfigureAwait(false);
            await SendStatusRequestAsync(stream, token).ConfigureAwait(false);

            var json = await ReadStatusResponseAsync(stream, token).ConfigureAwait(false);
            stopwatch.Stop();

            return ParseStatus(json, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ServerStatus.Offline("连接超时，服务器可能离线或屏蔽了查询。", StatusSource.Ping);
        }
        catch (Exception ex)
        {
            return ServerStatus.Offline($"无法连接服务器：{ex.Message}", StatusSource.Ping);
        }
    }

    private static async Task SendHandshakeAsync(Stream stream, string host, int port, CancellationToken token)
    {
        using var payload = new MemoryStream();
        VarInt.Write(payload, PacketIdHandshake);
        VarInt.Write(payload, HandshakeProtocolVersion);
        WriteString(payload, host);
        payload.WriteByte((byte)(port >> 8));
        payload.WriteByte((byte)(port & 0xFF));
        VarInt.Write(payload, NextStateStatus);

        await WritePacketAsync(stream, payload, token).ConfigureAwait(false);
    }

    private static async Task SendStatusRequestAsync(Stream stream, CancellationToken token)
    {
        using var payload = new MemoryStream();
        VarInt.Write(payload, PacketIdStatusRequest);
        await WritePacketAsync(stream, payload, token).ConfigureAwait(false);
    }

    private static async Task WritePacketAsync(Stream stream, MemoryStream payload, CancellationToken token)
    {
        var buffer = new byte[VarInt.EncodeSize((int)payload.Length) + payload.Length];
        var offset = VarInt.WriteTo(buffer, (int)payload.Length);
        payload.Position = 0;
        NetCompat.ReadExactly(payload, buffer, offset, (int)payload.Length);

        await stream.WriteAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        VarInt.Write(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<string> ReadStatusResponseAsync(Stream stream, CancellationToken token)
    {
        var length = await VarInt.ReadAsync(stream, token).ConfigureAwait(false);
        if (length <= 0)
        {
            throw new InvalidDataException("服务器未返回状态数据。");
        }

        var buffer = new byte[length];
        await NetCompat.ReadExactlyAsync(stream, buffer, 0, length, token).ConfigureAwait(false);

        using var reader = new MemoryStream(buffer);
        var packetId = await VarInt.ReadAsync(reader, token).ConfigureAwait(false);
        if (packetId != PacketIdStatusRequest)
        {
            throw new InvalidDataException($"状态响应包 ID 异常：{packetId}。");
        }

        var jsonLength = await VarInt.ReadAsync(reader, token).ConfigureAwait(false);
        if (jsonLength <= 0 || jsonLength > reader.Length - reader.Position)
        {
            throw new InvalidDataException("状态响应长度异常。");
        }

        var jsonBytes = new byte[jsonLength];
        NetCompat.ReadExactly(reader, jsonBytes, 0, jsonLength);
        return Encoding.UTF8.GetString(jsonBytes);
    }

    private static ServerStatus ParseStatus(string json, long latencyMs)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        int? online = null;
        int? max = null;
        var players = new List<string>();
        if (root.TryGetProperty("players", out var playersElement))
        {
            if (playersElement.TryGetProperty("online", out var onlineElement) && onlineElement.TryGetInt32(out var onlineValue))
            {
                online = onlineValue;
            }

            if (playersElement.TryGetProperty("max", out var maxElement) && maxElement.TryGetInt32(out var maxValue))
            {
                max = maxValue;
            }

            if (playersElement.TryGetProperty("sample", out var sample) && sample.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in sample.EnumerateArray())
                {
                    if (entry.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                    {
                        var name = nameElement.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            players.Add(name!);
                        }
                    }
                }
            }
        }

        string? versionName = null;
        if (root.TryGetProperty("version", out var versionElement) &&
            versionElement.TryGetProperty("name", out var versionNameElement) &&
            versionNameElement.ValueKind == JsonValueKind.String)
        {
            versionName = versionNameElement.GetString();
        }

        string? motd = null;
        if (root.TryGetProperty("description", out var description))
        {
            // MOTD 要**保留** § 颜色代码与换行（卡片/详情按原色渲染）。
            // 纯文本场景（搜索/筛选/日志）各自再调 StripFormatting / Normalize。
            var formatted = MinecraftText.KeepFormatting(MinecraftText.FlattenFormatted(description)).Trim();
            motd = string.IsNullOrWhiteSpace(MinecraftText.StripFormatting(formatted)) ? null : formatted;
        }

        string? favicon = null;
        if (root.TryGetProperty("favicon", out var faviconElement) && faviconElement.ValueKind == JsonValueKind.String)
        {
            favicon = faviconElement.GetString();
        }

        return new ServerStatus
        {
            IsOnline = true,
            OnlinePlayers = online,
            MaxPlayers = max,
            LatencyMs = latencyMs,
            VersionName = versionName,
            Motd = motd,
            FaviconDataUri = favicon,
            Players = players,
            Source = StatusSource.Ping,
        };
    }
}
