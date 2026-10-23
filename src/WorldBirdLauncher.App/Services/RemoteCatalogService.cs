using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.App.Services;

/// <summary>一次"有更新并且已经落盘"的通知（给界面决定什么时候应用）。</summary>
public sealed record CatalogUpdate(int FromRevision, int ToRevision, int Added, int Removed, string? Note)
{
    public bool IsEmpty => Added == 0 && Removed == 0;
}

/// <summary>
/// 远端目录服务：启动时/定时/手动 检查版本 → 拉增量（或全量）→ 原子写缓存 → 通知界面。
///
/// 更新策略（用户确认）：拿到并落盘后，如果用户**不在**服务器列表页就直接应用；
/// 正在看列表页则弹窗问"是否立即应用"。
/// </summary>
public sealed class RemoteCatalogService
{
    private readonly CatalogStore _store;
    private readonly CatalogApiClient _api;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RemoteCatalogService(CatalogStore store, CatalogApiClient? api = null)
    {
        _store = store;
        _api = api ?? new CatalogApiClient();
    }

    /// <summary>
    /// 判断"这条被下架了但要不要留在本地"（收藏过 / 玩过就留）。
    /// 由 AppSession 注入（要查本地收藏与游玩历史）。
    /// </summary>
    public Func<string, bool>? ShouldRetainRemoved { get; set; }

    /// <summary>有更新且已写入缓存时触发（在后台线程）。</summary>
    public event Action<CatalogUpdate>? UpdateReady;

    /// <summary>服务端下发了新的提示文案时触发（界面据此刷新文案列表）。</summary>
    public event Action? TipsUpdated;

    public string Endpoint => _api.Endpoint;

    public int Revision => _store.Revision;

    public DateTimeOffset? LastUpdatedAt => _store.LastUpdatedAt;

    public bool HasCache => _store.HasCache;

    public string DeviceId => _store.DeviceId;

    /// <summary>最近一次检查的结果文案（设置页/状态栏显示）。</summary>
    public string LastCheckMessage { get; private set; } = "尚未检查";

    public DateTimeOffset? LastCheckAt { get; private set; }

    /// <summary>服务端下发的全站热度榜单（id → 分值）；取不到就为空，界面退回本地热度。</summary>
    public IReadOnlyList<CatalogApiClient.HeatScoreDto> HeatRanking { get; private set; } = [];

    /// <summary>
    /// 排序加成（置顶/刷新卡效果，id → 加成信息）；列表页的 5 种排序都会"先按加成、再按原排序键"。
    /// 只影响服务器列表页，启动页热门推荐不受影响（2026-10-09 第五十一轮）。
    /// </summary>
    public IReadOnlyDictionary<string, CatalogApiClient.BoostDto> Boosts { get; private set; } =
        new Dictionary<string, CatalogApiClient.BoostDto>(StringComparer.OrdinalIgnoreCase);

    /// <summary>拉一次排序加成（失败保留上一次的结果）。</summary>
    public async Task<bool> RefreshBoostsAsync(CancellationToken token = default)
    {
        try
        {
            var result = await _api.GetBoostsAsync(token).ConfigureAwait(false);
            if (result.Failed || result.Value?.Data is not { } list)
            {
                return false;
            }

            var map = new Dictionary<string, CatalogApiClient.BoostDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var boost in list)
            {
                if (boost.Until > DateTimeOffset.Now)
                {
                    map[boost.ServerId] = boost;
                }
            }

            Boosts = map;
            return true;
        }
        catch (Exception ex)
        {
            App.LogLine($"拉取排序加成失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>拉一次全站热度榜单（失败保留上一次的结果）。</summary>
    public async Task<bool> RefreshHeatAsync(int limit = 100, CancellationToken token = default)
    {
        try
        {
            var result = await _api.GetHeatRankingAsync(limit, token).ConfigureAwait(false);
            if (result.Failed || result.Value is null)
            {
                return false;
            }

            HeatRanking = result.Value;
            return true;
        }
        catch (Exception ex)
        {
            App.LogLine($"拉取热度榜单失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查一次是否有新版本；返回 true 表示"本地数据发生了变化"。
    /// 服务端不可达时返回 false 并把原因写进 <see cref="LastCheckMessage"/>，不影响已有缓存。
    /// </summary>
    public async Task<bool> CheckAsync(bool force = false, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            LastCheckAt = DateTimeOffset.Now;

            var manifestResult = await _api.GetManifestAsync(force ? null : _store.Etag, token).ConfigureAwait(false);
            if (manifestResult.Failed)
            {
                LastCheckMessage = _store.HasCache
                    ? $"无法连接服务端，继续使用本地缓存（revision {_store.Revision}）"
                    : "无法连接服务端，且本地没有缓存数据";
                return false;
            }

            if (manifestResult.NotModified || manifestResult.Value is null)
            {
                LastCheckMessage = $"无更新（revision {_store.Revision}）";
                // 目录没变但提示文案/热度榜单可能已经变过，顺带刷新（拉不到不影响）
                await RefreshAuxiliaryAsync(token).ConfigureAwait(false);
                return false;
            }

            var manifest = manifestResult.Value;
            if (!force && manifest.Revision <= _store.Revision)
            {
                LastCheckMessage = $"无更新（revision {_store.Revision}）";
                await RefreshAuxiliaryAsync(token).ConfigureAwait(false);
                return false;
            }

            var from = _store.Revision;
            var entries = _store.LoadEntries().ToList();
            var added = 0;
            var removed = 0;

            if (from <= 0 || entries.Count == 0)
            {
                // 没有缓存：直接拉全量
                var fullResult = await _api.GetFullCatalogAsync(null, token).ConfigureAwait(false);
                if (fullResult.Value is null)
                {
                    LastCheckMessage = "拉取全量目录失败（服务端无响应）";
                    return false;
                }

                var full = fullResult.Value;
                added = full.Servers.Count;
                _store.Save(full);
            }
            else
            {
                var changesResult = await _api.GetChangesAsync(from, token).ConfigureAwait(false);
                if (changesResult.Value is null || changesResult.Value.Truncated)
                {
                    // 增量不可用（落后太多/服务端没给）：退回全量
                    var fullResult = await _api.GetFullCatalogAsync(null, token).ConfigureAwait(false);
                    if (fullResult.Value is null)
                    {
                        LastCheckMessage = "拉取全量目录失败（服务端无响应）";
                        return false;
                    }

                    var full = fullResult.Value;
                    added = full.Servers.Count;
                    _store.Save(full);
                }
                else
                {
                    var changes = changesResult.Value;
                    var byId = entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
                    foreach (var upsert in changes.Upserts)
                    {
                        if (byId.ContainsKey(upsert.Id))
                        {
                            byId[upsert.Id] = upsert;
                        }
                        else
                        {
                            byId[upsert.Id] = upsert;
                            added++;
                        }
                    }

                    foreach (var removal in changes.Removals)
                    {
                        if (!byId.Remove(removal, out var removedEntry))
                        {
                            continue;
                        }

                        removed++;
                        // 收藏过 / 玩过的条目本地保留（显示为"离线"，可手动移除）
                        var retain = ShouldRetainRemoved;
                        if (retain?.Invoke(removal) == true && removedEntry is not null)
                        {
                            _store.RetainRemoved([removedEntry], retain);
                        }
                    }

                    _store.Save(new CatalogDocumentDto(
                        changes.ToRevision, manifest.Etag, manifest.UpdatedAt, byId.Values.ToList()));
                }
            }

            LastCheckMessage = $"已更新到 revision {_store.Revision}（+{added} / -{removed}）";
            UpdateReady?.Invoke(new CatalogUpdate(from, _store.Revision, added, removed, manifest.Note));

            // 顺带拉一次提示文案（失败不影响目录更新）
            await RefreshAuxiliaryAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastCheckMessage = $"检查更新失败：{ex.Message}";
            App.LogLine($"检查目录更新失败：{ex}");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>提示文案 + 热度榜单：与目录版本无关，每次检查都顺带刷新一次。</summary>
    private async Task RefreshAuxiliaryAsync(CancellationToken token)
    {
        await RefreshTipsAsync(token).ConfigureAwait(false);
        await RefreshHeatAsync(token: token).ConfigureAwait(false);
    }

    /// <summary>把服务端的提示文案缓存到本地（<c>%APPDATA%\WorldBirdLauncher\catalog\tips.json</c>）。</summary>
    public async Task<bool> RefreshTipsAsync(CancellationToken token = default)
    {
        try
        {
            var result = await _api.GetTipsAsync(token).ConfigureAwait(false);
            var tips = result.Value?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (result.Failed || tips is null || tips.Count == 0)
            {
                return false;
            }

            var directory = Path.GetDirectoryName(_store.MetaPath)!;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "tips.json");
            var temporary = path + ".tmp";
            await NetCompat.WriteAllTextAsync(
                temporary, System.Text.Json.JsonSerializer.Serialize(tips), token).ConfigureAwait(false);
            NetCompat.MoveFile(temporary, path, overwrite: true);
            TipsUpdated?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            App.LogLine($"拉取提示文案失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>取一批服务器的状态（只含信号等级）；失败时返回空表。</summary>
    public Task<IReadOnlyList<CatalogStatusDto>> GetStatusesAsync(IEnumerable<string> ids, CancellationToken token = default) =>
        _api.GetStatusesAsync(ids, token);

    /// <summary>
    /// 上报一条匿名使用指标（打开次数 / 会话时长 / 到启动游戏耗时）。
    /// 失败只记日志，不影响任何界面（2026-10-09 第五十轮）。
    /// </summary>
    public Task<bool> ReportMetricAsync(string kind, int? durationSeconds = null, string? serverId = null, CancellationToken token = default) =>
        _api.SendMetricAsync(_store.DeviceId, kind, durationSeconds, serverId, token);

    /// <summary>异步上报一个事件（热度/崩溃摘要）；失败只记日志，不打扰用户。</summary>
    public async Task ReportEventAsync(string kind, string? serverId)
    {
        try
        {
            await _api.SendEventAsync(_store.DeviceId, kind, serverId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogLine($"上报 {kind} 事件失败：{ex.Message}");
        }
    }

    /// <summary>提交服务器（宣传贴 / 手动填写）。</summary>
    public Task<(bool Ok, string Message)> SubmitServerAsync(string mode, string? threadUrl, ServerEntry? entry) =>
        _api.SubmitServerAsync(mode, threadUrl, entry, _store.DeviceId);
}
