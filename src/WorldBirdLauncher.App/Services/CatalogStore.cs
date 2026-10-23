using System.Text.Json;
using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.App.Services;

/// <summary>本地缓存的元信息（版本号 / ETag / 更新时间 / 匿名设备号）。</summary>
public sealed record CatalogMeta(int Revision, string Etag, DateTimeOffset UpdatedAt, string DeviceId);

/// <summary>
/// 目录缓存：客户端**唯一**的服务器列表数据来源（列表只走远端下发）。
///
/// 落盘位置：<c>%APPDATA%\WorldBirdLauncher\catalog\catalog.json</c>（+ catalog.meta.json），
/// 写入是"临时文件 → 覆盖正式文件"的原子替换，并保留上一份 <c>catalog.previous.json</c> 以便回滚。
/// </summary>
public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root;

    public CatalogStore(string? root = null)
    {
        _root = root ?? Path.Combine(AppSettings.Directory, "catalog");
        Directory.CreateDirectory(_root);
        Meta = LoadMeta();
    }

    public string Directory_ => _root;

    public string CatalogPath => Path.Combine(_root, "catalog.json");

    public string MetaPath => Path.Combine(_root, "catalog.meta.json");

    /// <summary>被服务端下架、但因为"收藏过 / 玩过"而保留在本地的条目。</summary>
    public string RetainedPath => Path.Combine(_root, "retained.json");

    public CatalogMeta Meta { get; private set; }

    public int Revision => Meta.Revision;

    public string Etag => Meta.Etag;

    public DateTimeOffset? LastUpdatedAt => Meta.UpdatedAt == default ? null : Meta.UpdatedAt;

    /// <summary>匿名设备号：首次运行随机生成并持久化，不含任何账号信息。</summary>
    public string DeviceId => Meta.DeviceId;

    public bool HasCache => File.Exists(CatalogPath);

    /// <summary>读缓存里的服务器条目；没有缓存或文件损坏时返回空列表（界面会提示"无法连接"）。</summary>
    public IReadOnlyList<ServerEntry> LoadEntries()
    {
        var entries = new List<ServerEntry>();
        try
        {
            if (File.Exists(CatalogPath))
            {
                var document = JsonSerializer.Deserialize<CatalogDocumentDto>(File.ReadAllText(CatalogPath), Json);
                entries.AddRange(document?.Servers?.Where(e => !string.IsNullOrWhiteSpace(e.Id)) ?? []);
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"读取目录缓存失败：{ex.Message}");
        }

        // 把"已下架但用户收藏过/玩过"的条目接在后面（界面按"离线"展示）
        foreach (var retained in LoadRetained())
        {
            if (entries.All(e => !string.Equals(e.Id, retained.Id, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(retained);
            }
        }

        return entries;
    }

    /// <summary>读"保留条目"清单。</summary>
    public IReadOnlyList<ServerEntry> LoadRetained()
    {
        try
        {
            if (!File.Exists(RetainedPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<ServerEntry>>(File.ReadAllText(RetainedPath), Json) ?? [];
        }
        catch (Exception ex)
        {
            App.LogLine($"读取保留条目失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 服务端把条目下架（非活跃/归档）时调用：如果用户收藏过或玩过，就把这条**留在本地**，
    /// 只显示为"离线"，用户可以在详情页手动移除。返回被保留的条数。
    /// </summary>
    public int RetainRemoved(IEnumerable<ServerEntry> removed, Func<string, bool> shouldRetain)
    {
        var retained = LoadRetained().ToList();
        var added = 0;
        foreach (var entry in removed)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !shouldRetain(entry.Id))
            {
                continue;
            }

            if (retained.Any(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            retained.Add(entry);
            added++;
        }

        if (added > 0)
        {
            SaveRetained(retained);
        }

        return added;
    }

    /// <summary>用户手动移除一条保留条目。</summary>
    public void DropRetained(string serverId)
    {
        var retained = LoadRetained()
            .Where(e => !string.Equals(e.Id, serverId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveRetained(retained);
    }

    private void SaveRetained(IReadOnlyList<ServerEntry> retained)
    {
        try
        {
            File.WriteAllText(RetainedPath, JsonSerializer.Serialize(retained, Json));
        }
        catch (Exception ex)
        {
            App.LogLine($"保存保留条目失败：{ex.Message}");
        }
    }

    /// <summary>原子写入一份新的目录文档（同时保留上一份做回滚）。</summary>
    public void Save(CatalogDocumentDto document)
    {
        var temporary = CatalogPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));

        if (File.Exists(CatalogPath))
        {
            File.Copy(CatalogPath, Path.Combine(_root, "catalog.previous.json"), overwrite: true);
        }

        NetCompat.MoveFile(temporary, CatalogPath, overwrite: true);

        Meta = Meta with
        {
            Revision = document.Revision,
            Etag = document.Etag,
            UpdatedAt = document.UpdatedAt,
        };
        SaveMeta(Meta);
    }

    private CatalogMeta LoadMeta()
    {
        try
        {
            if (File.Exists(MetaPath))
            {
                var meta = JsonSerializer.Deserialize<CatalogMeta>(File.ReadAllText(MetaPath), Json);
                if (meta is not null)
                {
                    // 老版本的 meta 里可能没有 deviceId，补一个
                    return meta.DeviceId is { Length: > 0 } ? meta : meta with { DeviceId = NewDeviceId() };
                }
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"读取目录缓存元信息失败：{ex.Message}");
        }

        var fresh = new CatalogMeta(0, string.Empty, default, NewDeviceId());
        SaveMeta(fresh);
        return fresh;
    }

    private void SaveMeta(CatalogMeta meta)
    {
        try
        {
            File.WriteAllText(MetaPath, JsonSerializer.Serialize(meta, Json));
        }
        catch (Exception ex)
        {
            App.LogLine($"写入目录缓存元信息失败：{ex.Message}");
        }
    }

    private static string NewDeviceId() => Guid.NewGuid().ToString("N");
}
