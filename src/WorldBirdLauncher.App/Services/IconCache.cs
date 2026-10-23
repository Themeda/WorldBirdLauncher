using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 服务器图标缓存：内存 → 本地磁盘（<c>%APPDATA%\WorldBirdLauncher\catalog\icons</c>）→ 服务端。
///
/// 按需求：
/// - 只给**列表里已渲染（可见）的卡片**按需拉取，不整表下载；
/// - 服务端连不上时用**上一次缓存到的图标**（磁盘里保留了就继续用），不会因为一时离线变回首字母。
/// </summary>
public sealed class IconCache
{
    /// <summary>
    /// `_memory` 自己的锁。
    ///
    /// ★ 踩过的坑：`_gate` 是 `SemaphoreSlim(4, 4)`（目的是"最多 4 个并发请求"），
    /// 它**不是互斥锁**——4 个任务可以同时进临界区，`Dictionary` 并发写会把内部结构写坏，
    /// 紧接着的 `_memory[serverId]` 取值就抛 `KeyNotFoundException`（中文"给定关键字不在字典中"，
    /// 服务端不可用时一轮渲染 4 张图标必现）。所以内存字典必须再单独加锁，且**读写都要在锁里**。
    /// </summary>
    private readonly object _memoryGate = new();
    private readonly Dictionary<string, byte[]?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(4, 4);
    private readonly string _root;
    private readonly CatalogApiClient _api;

    public IconCache(CatalogApiClient api, string? root = null)
    {
        _api = api;
        _root = root ?? Path.Combine(AppSettings.Directory, "catalog", "icons");
        Directory.CreateDirectory(_root);
    }

    /// <summary>取图标（没有就返回 null）。会先看内存/磁盘，必要时才请求服务端。</summary>
    public async Task<byte[]?> GetAsync(string serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        lock (_memoryGate)
        {
            if (_memory.TryGetValue(serverId, out var cached))
            {
                return cached;
            }
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = PathFor(serverId);
            if (File.Exists(path))
            {
                var bytes = await NetCompat.ReadAllBytesAsync(path).ConfigureAwait(false);
                var value = bytes.Length > 0 ? bytes : null;
                Remember(serverId, value);
                return value;
            }

            var fetched = await _api.GetIconAsync(serverId).ConfigureAwait(false);
            if (fetched is null || fetched.Length == 0)
            {
                Remember(serverId, null); // 服务端也没有：记住这次结果，避免反复请求
                return null;
            }

            await NetCompat.WriteAllBytesAsync(path, fetched).ConfigureAwait(false);
            Remember(serverId, fetched);
            return fetched;
        }
        catch (Exception ex)
        {
            App.LogLine($"取服务器图标失败（{serverId}）：{ex.Message}");
            Remember(serverId, null);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>写内存缓存（**必须**走这里，别直接索引字典）。</summary>
    private void Remember(string serverId, byte[]? bytes)
    {
        lock (_memoryGate)
        {
            _memory[serverId] = bytes;
        }
    }

    private string PathFor(string serverId)
    {
        // 目录名可能带奇怪字符，用哈希做文件名最稳
        var hash = NetCompat.ToHexString(NetCompat.Sha256(Encoding.UTF8.GetBytes(serverId)))[..32].ToLowerInvariant();
        return Path.Combine(_root, hash + ".png");
    }
}
