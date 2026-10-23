using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using McServerList.Core.Models;
using McServerList.Core.Networking;

namespace McServerList.App.Services;

/// <summary>
/// 本机实测信号缓存（用户 2026-10-10）。
///
/// 背景：列表里的「按信号」排序应该是**玩家电脑 ↔ 游戏服务器**之间的信号，
/// 而不是"服务端后台 ↔ 游戏服务器"的采样。所以客户端自己 ping 一遍，把结果留在这里。
///
/// 失效规则（用户确认）：**以服务端下发的信号等级为准**——
/// 只在该服务器"服务端下发的等级"发生变化时才重新测量；一直不变就不再重复 ping，
/// 详情页手动测速则总是以最新结果覆盖。
/// </summary>
public sealed record LocalLatencyEntry(
    string ServerId,
    string Level,
    long? Milliseconds,
    string ServerLevel,
    DateTimeOffset MeasuredAt);

public sealed class LocalLatencyCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalLatencyEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public LocalLatencyCache(string? path = null)
    {
        _path = path ?? Path.Combine(AppSettings.Directory, "latency.json");
        Load();
    }

    public LocalLatencyEntry? Get(string serverId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(serverId, out var entry) ? entry : null;
        }
    }

    public void Set(LocalLatencyEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.ServerId] = entry;
        }

        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var text = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, LocalLatencyEntry>>(text, Json);
            if (parsed is null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (var pair in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key))
                    {
                        _entries[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"读取本机测速缓存失败：{ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<string, LocalLatencyEntry> snapshot;
            lock (_gate)
            {
                snapshot = new Dictionary<string, LocalLatencyEntry>(_entries, StringComparer.OrdinalIgnoreCase);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, Json));
        }
        catch (Exception ex)
        {
            App.LogLine($"写入本机测速缓存失败：{ex.Message}");
        }
    }
}

/// <summary>本机测速：只做 TCP 连接握手，拿到延迟就够算信号等级了（不用完整 Server List Ping）。</summary>
public static class LocalLatencyProbe
{
    private static readonly MinecraftSrvResolver Resolver = new();

    public static async Task<(string Level, long? Milliseconds)> MeasureAsync(
        ServerEntry entry, TimeSpan timeout, CancellationToken token = default)
    {
        if (!entry.HasAddress)
        {
            return (StatusLevels.NoAddress, null);
        }

        try
        {
            var (host, port) = await Resolver
                .ResolveAsync(entry.Address, entry.Port).ConfigureAwait(false);

            using var client = new TcpClient();
            var stopwatch = Stopwatch.StartNew();
            var connect = client.ConnectAsync(host, port);
            var finished = await Task.WhenAny(connect, Task.Delay(timeout, token)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, connect) || !client.Connected)
            {
                return (StatusLevels.Offline, null);
            }

            stopwatch.Stop();
            var ms = stopwatch.ElapsedMilliseconds;
            return (StatusLevels.FromLatency(ms), ms);
        }
        catch (Exception)
        {
            return (StatusLevels.Offline, null);
        }
    }
}
