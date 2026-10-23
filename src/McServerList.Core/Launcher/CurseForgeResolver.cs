using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>解析出来的 CurseForge 文件（下载地址 + 真实文件名）。</summary>
public sealed record ResolvedCurseFile(string Url, string? Name);

/// <summary>
/// CurseForge 下载地址解析器：客户端**不内置 API Key**，把 `modId/fileId` 交给我们的服务端，
/// 由服务端带 key 去官方 API 换地址（见服务端 <c>/api/v1/curseforge/resolve</c>）。
/// </summary>
public interface ICurseForgeUrlResolver
{
    Task<IReadOnlyDictionary<string, ResolvedCurseFile>> ResolveAsync(
        IReadOnlyList<(string ModId, string FileId)> files,
        CancellationToken cancellationToken = default);
}

/// <summary>走我们服务端的实现（服务器地址与客户端 key 由调用方注入）。</summary>
public sealed class ServerCurseForgeResolver : ICurseForgeUrlResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int ChunkSize = 100; // 服务端单次上限 200，这里留点余量

    private readonly HttpClient _http;

    public ServerCurseForgeResolver(string endpoint, string clientKey)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Client-Key", clientKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("McServerListLauncher/1.0");
    }

    public async Task<IReadOnlyDictionary<string, ResolvedCurseFile>> ResolveAsync(
        IReadOnlyList<(string ModId, string FileId)> files,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ResolvedCurseFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in files.Distinct().Chunk(ChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var payload = JsonSerializer.Serialize(
                new { files = chunk.Select(f => $"{f.ModId}/{f.FileId}").ToList() },
                Json);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http
                .PostAsync("api/v1/curseforge/resolve", content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                continue; // 服务端不可用/限流：这一批就当解析不到，交给上层记成"未能下载"
            }

            // net48 的 Stream 没有 IAsyncDisposable，只能同步 using（AGENTS.md 第 34 条）
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("files", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var entry in map.EnumerateObject())
            {
                var url = entry.Value.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var name = entry.Value.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                result[entry.Name] = new ResolvedCurseFile(url!, name);
            }
        }

        return result;
    }
}
