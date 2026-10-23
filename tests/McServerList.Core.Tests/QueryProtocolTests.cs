using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using McServerList.Core.Models;
using McServerList.Core.Protocol;

namespace McServerList.Core.Tests;

public class QueryProtocolTests
{
    [Fact]
    public async Task Query_能解析详细状态与完整玩家列表()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        var fields = new Dictionary<string, string>
        {
            ["hostname"] = "§6测试服务器",
            ["gametype"] = "SMP",
            ["game_id"] = "MINECRAFT",
            ["version"] = "1.20.4",
            ["plugins"] = "Essentials:1.0; Vault:1.7",
            ["map"] = "world",
            ["numplayers"] = "7",
            ["maxplayers"] = "60",
            ["hostport"] = port.ToString(),
        };
        string[] players = ["Steve", "Alex"];

        _ = Task.Run(async () =>
        {
            var handshake = await server.ReceiveAsync();
            await server.SendAsync(BuildChallengeResponse(handshake.Buffer, "1234567"), handshake.RemoteEndPoint);

            var statRequest = await server.ReceiveAsync();
            var sessionId = BinaryPrimitives.ReadInt32BigEndian(statRequest.Buffer.AsSpan(3, 4));
            await server.SendAsync(BuildFullStatResponse(sessionId, fields, players), statRequest.RemoteEndPoint);
        });

        var status = await new QueryProtocol().QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));

        Assert.True(status.IsOnline, status.Error);
        Assert.Equal(7, status.OnlinePlayers);
        Assert.Equal(60, status.MaxPlayers);
        Assert.Equal("1.20.4", status.VersionName);
        Assert.Equal("测试服务器", status.HostName);
        Assert.Equal("world", status.Map);
        Assert.Equal("SMP", status.GameType);
        Assert.Equal("Essentials:1.0; Vault:1.7", status.Plugins);
        Assert.Equal(players, status.Players);
        Assert.Equal(StatusSource.Query, status.Source);
    }

    [Fact]
    public async Task Query_无响应时返回离线状态()
    {
        var status = await new QueryProtocol().QueryAsync("127.0.0.1", 1, TimeSpan.FromMilliseconds(1500));

        Assert.False(status.IsOnline);
        Assert.Equal(StatusSource.Query, status.Source);
    }

    private static byte[] BuildChallengeResponse(byte[] request, string challenge)
    {
        var result = new List<byte> { 0x09 };
        result.AddRange(request[3..7]);
        result.AddRange(Encoding.ASCII.GetBytes(challenge));
        result.Add(0x00);
        return result.ToArray();
    }

    private static byte[] BuildFullStatResponse(int sessionId, Dictionary<string, string> fields, string[] players)
    {
        var result = new List<byte> { 0x00 };
        var sessionBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(sessionBytes, sessionId);
        result.AddRange(sessionBytes);

        // "splitnum" + 0x00 + 0x80 + 0x00
        result.AddRange([0x73, 0x70, 0x6C, 0x69, 0x74, 0x6E, 0x75, 0x6D, 0x00, 0x80, 0x00]);

        foreach (var (key, value) in fields)
        {
            result.AddRange(Encoding.UTF8.GetBytes(key));
            result.Add(0x00);
            result.AddRange(Encoding.UTF8.GetBytes(value));
            result.Add(0x00);
        }

        result.Add(0x00);          // 键值对结束标记
        result.AddRange(new byte[10]); // 填充
        result.AddRange(Encoding.ASCII.GetBytes("player_"));
        result.Add(0x00);
        result.Add(0x00);
        foreach (var player in players)
        {
            result.AddRange(Encoding.UTF8.GetBytes(player));
            result.Add(0x00);
        }

        result.Add(0x00);
        return result.ToArray();
    }
}
