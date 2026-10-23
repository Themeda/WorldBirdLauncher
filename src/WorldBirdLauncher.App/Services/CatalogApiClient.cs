using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.App.Services;

/// <summary>目录版本清单（对应服务端 <c>CatalogManifest</c>）。</summary>
public sealed record CatalogManifestDto(
    int Revision,
    string Etag,
    DateTimeOffset UpdatedAt,
    int ServerCount,
    int AddressCount,
    string? Note);

/// <summary>全量目录（也是本地缓存的落盘格式）。</summary>
public sealed record CatalogDocumentDto(
    int Revision,
    string Etag,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ServerEntry> Servers);

/// <summary>增量变更（对应服务端 <c>CatalogChanges</c>）。</summary>
public sealed record CatalogChangesDto(
    int FromRevision,
    int ToRevision,
    bool Truncated,
    IReadOnlyList<ServerEntry> Upserts,
    IReadOnlyList<string> Removals);

/// <summary>
/// 服务端状态：只给信号等级（good/fair/poor/offline），没有具体延迟。
/// <c>Motd</c> 是采样到的原始 MOTD（保留 § 颜色代码与换行，卡片/详情展示用）。
/// </summary>
public sealed record CatalogStatusDto(
    string Id,
    bool Online,
    string Level,
    int? PlayersOnline,
    int? PlayersMax,
    string? VersionText,
    DateTimeOffset SampledAt,
    string? Motd);

/// <summary>
/// 一次请求的结果：<c>Failed</c> = 连不上/服务端报错（重试过 3 次），
/// <c>NotModified</c> = 服务端 304（本地还是最新的），两者必须分开——
/// 否则界面会把"服务端不可达"显示成"无更新"。
/// </summary>
public sealed record FetchResult<T>(bool Failed, bool NotModified, T? Value)
    where T : class
{
    public static FetchResult<T> Ok(T value) => new(false, false, value);

    public static FetchResult<T> Unchanged() => new(false, true, null);

    public static FetchResult<T> Error() => new(true, false, null);
}

/// <summary>
/// 目录服务端的 HTTP 客户端：版本清单 / 全量 / 增量 / 状态 / 事件上报。
/// 失败按 1s、4s、10s 退避重试 3 次（不弹窗，只记日志）。
/// </summary>
public sealed class CatalogApiClient
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(10),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public CatalogApiClient(string? endpoint = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri((endpoint ?? AppInfo.CatalogEndpoint).TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.2");
    }

    public string Endpoint => _http.BaseAddress?.ToString() ?? string.Empty;

    /// <summary>取版本清单（服务端 304 时 <see cref="FetchResult{T}.NotModified"/> 为 true）。</summary>
    public Task<FetchResult<CatalogManifestDto>> GetManifestAsync(string? etag, CancellationToken token = default) =>
        SendAsync<CatalogManifestDto>("api/v1/catalog/manifest", etag, token);

    /// <summary>取全量目录。</summary>
    public Task<FetchResult<CatalogDocumentDto>> GetFullCatalogAsync(string? etag, CancellationToken token = default) =>
        SendAsync<CatalogDocumentDto>("api/v1/catalog", etag, token);

    /// <summary>取增量变更（正常路径）。</summary>
    public Task<FetchResult<CatalogChangesDto>> GetChangesAsync(int since, CancellationToken token = default) =>
        SendAsync<CatalogChangesDto>($"api/v1/catalog/changes?since={since}", null, token);

    /// <summary>按 id 批量取状态（列表/详情用）。</summary>
    public async Task<IReadOnlyList<CatalogStatusDto>> GetStatusesAsync(
        IEnumerable<string> ids, CancellationToken token = default)
    {
        var list = ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return [];
        }

        // 一次别带太多 id，避免 URL 过长
        var result = new List<CatalogStatusDto>();
        foreach (var chunk in list.Chunk(100))
        {
            var query = string.Join(",", chunk.Select(Uri.EscapeDataString));
            var part = await SendAsync<List<CatalogStatusDto>>($"api/v1/status?ids={query}", null, token)
                .ConfigureAwait(false);
            if (part.Value is not null)
            {
                result.AddRange(part.Value);
            }
        }

        return result;
    }

    /// <summary>取游戏提示文案（拉不到时返回 Failed，客户端用内置那份兜底）。</summary>
    public Task<FetchResult<List<string>>> GetTipsAsync(CancellationToken token = default) =>
        SendAsync<List<string>>("api/v1/tips", null, token);

    /// <summary>取全站热度榜单。</summary>
    public Task<FetchResult<List<HeatScoreDto>>> GetHeatRankingAsync(int limit = 100, CancellationToken token = default) =>
        SendAsync<List<HeatScoreDto>>($"api/v1/heat?limit={NetCompat.Clamp(limit, 1, 500)}", null, token);

    public sealed record HeatScoreDto(string Id, double Score);

    /// <summary>服务端下发的排序加成（置顶/刷新卡效果，2026-10-09 第五十一轮）。</summary>
    /// <param name="HeatBonus">
    /// 刷新卡给"综合热度分"的一笔加分（服务端按 30 天半衰期算好；置顶卡为 0）。
    /// 用户 2026-10-10 确认：刷新卡只影响「按综合热度」排序。
    /// </param>
    public sealed record BoostDto(
        string ServerId,
        string Kind,
        int Rank,
        DateTimeOffset Until,
        DateTimeOffset? LastRefreshedAt,
        double HeatBonus = 0);

    /// <summary>排序加成的下发改成 { code, data } 包一层（和广告接口一致）。</summary>
    public sealed record BoostListDto(int Code, List<BoostDto> Data);

    /// <summary>取排序加成列表（很小，跟状态一起刷新）。</summary>
    public Task<FetchResult<BoostListDto>> GetBoostsAsync(CancellationToken token = default) =>
        SendAsync<BoostListDto>("api/v1/boosts", null, token);

    /// <summary>取服务器图标（PNG 字节）；404 表示服务端也没有这张图。</summary>
    public async Task<byte[]?> GetIconAsync(string serverId, CancellationToken token = default)
    {
        try
        {
            using var response = await _http
                .GetAsync($"api/v1/icon/{Uri.EscapeDataString(serverId)}", token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogLine($"请求图标 {serverId} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>提交一个服务器（宣传贴 / 手动填写），进服务端候选审核队列。</summary>
    public async Task<(bool Ok, string Message)> SubmitServerAsync(
        string mode, string? threadUrl, ServerEntry? entry, string deviceId, CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/submit")
            {
                Content = JsonContent.Create(new { mode, deviceId, threadUrl, entry }, options: Json),
            };
            request.Headers.Add("X-Client-Key", AppInfo.CatalogClientKey);
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            // net48 的 HttpStatusCode 枚举没有 TooManyRequests（429），直接比数值
            if ((int)response.StatusCode == 429)
            {
                return (false, "今天提交的次数已达上限，请明天再试。");
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"提交失败（{(int)response.StatusCode}）：{ExtractMessage(body)}");
            }

            return (true, ExtractMessage(body) is { Length: > 0 } message ? message : "已提交，等待人工审核。");
        }
        catch (Exception ex)
        {
            return (false, $"提交失败：{ex.Message}");
        }
    }

    private static string ExtractMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() ?? string.Empty
                : document.RootElement.TryGetProperty("error", out var error) ? error.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>上报一次事件（热度/崩溃摘要）。被去重或超限也算成功。</summary>
    public async Task<bool> SendEventAsync(
        string deviceId, string kind, string? serverId, CancellationToken token = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/events")
                {
                    Content = JsonContent.Create(new { deviceId, kind, serverId }, options: Json),
                };
                request.Headers.Add("X-Client-Key", AppInfo.CatalogClientKey);
                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length && !token.IsCancellationRequested)
            {
                App.LogLine($"上报事件失败（第 {attempt + 1} 次）：{ex.Message}");
                await Task.Delay(RetryDelays[attempt], token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.LogLine($"上报事件放弃：{ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 上报一条匿名使用指标（会话时长 / 打开次数 / 到启动游戏耗时）。
    /// 只带匿名设备号 + 时长 + 客户端版本 + 系统版本，**不含任何账号信息**（2026-10-09 第五十轮）。
    /// </summary>
    public async Task<bool> SendMetricAsync(
        string deviceId, string kind, int? durationSeconds, string? serverId = null, CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/metrics")
            {
                Content = JsonContent.Create(new
                {
                    deviceId,
                    kind,
                    durationSeconds,
                    serverId,
                    clientVersion = AppInfo.ClientVersion,
                    osVersion = Environment.OSVersion.VersionString,
                }, options: Json),
            };
            request.Headers.Add("X-Client-Key", AppInfo.CatalogClientKey);
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            App.LogLine($"上报指标 {kind} 失败：{ex.Message}");
            return false;
        }
    }

    private async Task<FetchResult<T>> SendAsync<T>(string relativeUrl, string? etag, CancellationToken token)
        where T : class
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
                if (!string.IsNullOrWhiteSpace(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{etag}\"");
                }

                using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return FetchResult<T>.Unchanged();
                }

                response.EnsureSuccessStatusCode();
                var value = await response.Content.ReadFromJsonAsync<T>(Json, token).ConfigureAwait(false);
                return value is null ? FetchResult<T>.Error() : FetchResult<T>.Ok(value);
            }
            catch (Exception ex) when (attempt < RetryDelays.Length && !token.IsCancellationRequested)
            {
                App.LogLine($"请求 {relativeUrl} 失败（第 {attempt + 1} 次）：{ex.Message}");
                await Task.Delay(RetryDelays[attempt], token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                App.LogLine($"请求 {relativeUrl} 放弃：{ex.Message}");
                return FetchResult<T>.Error();
            }
        }
    }
}
