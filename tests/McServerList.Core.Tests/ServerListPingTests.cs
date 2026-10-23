using System.Net;
using System.Net.Sockets;
using System.Text;
using McServerList.Core.Models;
using McServerList.Core.Protocol;

namespace McServerList.Core.Tests;

public class ServerListPingTests
{
    [Fact]
    public async Task 状态查询_能解析人数版本MOTD与图标()
    {
        const string json = """
        {
          "version": { "name": "Paper 1.20.4", "protocol": 765 },
          "players": { "online": 42, "max": 200 },
          "description": { "text": "§a欢迎 §e来到", "extra": [ { "text": " 测试服" } ] },
          "favicon": "data:image/png;base64,AAAA"
        }
        """;

        var (listener, port) = StartFakeServer(json);
        try
        {
            var status = await new ServerListPing().QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));

            Assert.True(status.IsOnline, status.Error);
            Assert.Equal(42, status.OnlinePlayers);
            Assert.Equal(200, status.MaxPlayers);
            Assert.Equal("Paper 1.20.4", status.VersionName);
            // 2026-10-08 付费模块：MOTD 改为**保留** § 颜色代码（卡片/详情要按原色渲染），
            // 需要纯文本时各自调 MinecraftText.StripFormatting / Normalize。
            Assert.Equal("§a欢迎 §e来到 测试服", status.Motd);
            Assert.Equal("data:image/png;base64,AAAA", status.FaviconDataUri);
            Assert.Equal(StatusSource.Ping, status.Source);
            Assert.NotNull(status.LatencyMs);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 状态查询_服务器离线时返回离线状态而不是抛异常()
    {
        var status = await new ServerListPing().QueryAsync("127.0.0.1", 1, TimeSpan.FromSeconds(2));

        Assert.False(status.IsOnline);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }

    private static (TcpListener Listener, int Port) StartFakeServer(string json)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadPacketAsync(stream); // 握手
            await ReadPacketAsync(stream); // 状态请求
            await stream.WriteAsync(BuildStatusResponse(json));
            await stream.FlushAsync();
        });

        return (listener, port);
    }

    private static async Task<byte[]> ReadPacketAsync(NetworkStream stream)
    {
        var length = 0;
        var shift = 0;
        var buffer = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(buffer.AsMemory(0, 1)) == 0)
            {
                throw new EndOfStreamException();
            }

            length |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0)
            {
                break;
            }

            shift += 7;
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return body;
    }

    private static byte[] BuildStatusResponse(string json)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var body = new List<byte> { 0x00 };
        body.AddRange(EncodeVarInt(jsonBytes.Length));
        body.AddRange(jsonBytes);

        var packet = new List<byte>();
        packet.AddRange(EncodeVarInt(body.Count));
        packet.AddRange(body);
        return packet.ToArray();
    }

    private static byte[] EncodeVarInt(int value)
    {
        var result = new List<byte>();
        var current = unchecked((uint)value);
        while (true)
        {
            if ((current & ~0x7Fu) == 0)
            {
                result.Add((byte)current);
                return result.ToArray();
            }

            result.Add((byte)((current & 0x7F) | 0x80));
            current >>= 7;
        }
    }
}
