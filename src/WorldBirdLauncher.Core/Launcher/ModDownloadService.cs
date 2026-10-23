using System.Net.Http;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>Modrinth 搜索命中的模组。</summary>
public sealed record ModSearchResult(
    string ProjectId,
    string Title,
    string Slug,
    string Description,
    string? IconUrl,
    long Downloads,
    string Author)
{
    public string DownloadsText => Downloads >= 1_000_000
        ? $"{Downloads / 1_000_000d:0.#}M"
        : Downloads >= 1000
            ? $"{Downloads / 1000d:0.#}k"
            : Downloads.ToString();
}

/// <summary>
/// 单个模组的搜索与下载（数据源：Modrinth 公开 API，无需 API Key）。
///
/// 说明：CurseForge 需要 API Key，暂不接入；CurseForge 格式的整合包仍按原逻辑处理。
/// </summary>
public sealed class ModDownloadService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Modrinth 要求带上可识别的 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.1 (github.com/Themeda/WorldBirdLauncher)");
        return client;
    }

    /// <summary>按关键字搜索模组，可按游戏版本与加载器过滤（加载器传 null 表示不限）。</summary>
    public async Task<IReadOnlyList<ModSearchResult>> SearchAsync(
        string query,
        string? gameVersion = null,
        string? loader = null,
        string projectType = "mod",
        CancellationToken cancellationToken = default)
    {
        var facets = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            facets.Add($"[\"versions:{gameVersion}\"]");
        }

        // 光影包（shader）不看加载器，只看游戏版本
        if (projectType == "mod" && !string.IsNullOrWhiteSpace(loader) && !string.Equals(loader, "原版", StringComparison.Ordinal))
        {
            // ★ 踩过的坑：slug 为空时**不能**拼成 "categories:"（空值）——Modrinth 会直接返回 400 Bad Request。
            //   （原来 LoaderSlug 只认首字母大写的 Fabric/Forge/NeoForge，传小写或 Quilt 就会命中空串。）
            var slug = LoaderSlug(loader);
            if (!string.IsNullOrWhiteSpace(slug))
            {
                facets.Add($"[\"categories:{slug}\"]");
            }
        }

        var url = "https://api.modrinth.com/v2/search"
                  + $"?query={Uri.EscapeDataString(query)}&limit=20&index=relevance"
                  + $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var results = new List<ModSearchResult>();
        if (!document.RootElement.TryGetProperty("hits", out var hits))
        {
            return results;
        }

        foreach (var hit in hits.EnumerateArray())
        {
            results.Add(new ModSearchResult(
                GetString(hit, "project_id"),
                GetString(hit, "title"),
                GetString(hit, "slug"),
                GetString(hit, "description"),
                hit.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String ? icon.GetString() : null,
                hit.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var value) ? value : 0,
                GetString(hit, "author")));
        }

        return results;
    }

    /// <summary>把指定模组下载到目标 mods 目录（自动挑选匹配游戏版本与加载器的最新文件）。</summary>
    public async Task<ModInstallOutcome> DownloadAsync(
        string projectId,
        string gameVersion,
        string loader,
        string modsDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(new InstallProgress("正在查询模组文件", 10));
            var loaderSlug = LoaderSlug(loader);
            var url = $"https://api.modrinth.com/v2/project/{projectId}/version"
                      + $"?game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}"
                      + (string.IsNullOrWhiteSpace(loaderSlug) ? string.Empty : $"&loaders={Uri.EscapeDataString($"[\"{loaderSlug}\"]")}");

            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                return ModInstallOutcome.Fail($"该模组没有适配 {gameVersion} / {loader} 的文件。");
            }

            var file = default(JsonElement?);
            var versionNumber = string.Empty;
            foreach (var version in document.RootElement.EnumerateArray())
            {
                if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                {
                    continue;
                }

                var candidate = files.EnumerateArray().FirstOrDefault(f =>
                    f.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
                if (candidate.ValueKind != JsonValueKind.Object)
                {
                    candidate = files[0];
                }

                file = candidate;
                versionNumber = GetString(version, "version_number");
                break;
            }

            if (file is null)
            {
                return ModInstallOutcome.Fail("没有找到可下载的模组文件。");
            }

            var fileName = GetString(file.Value, "filename");
            var downloadUrl = GetString(file.Value, "url");
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                return ModInstallOutcome.Fail("模组文件信息不完整。");
            }

            Directory.CreateDirectory(modsDirectory);
            var targetPath = Path.Combine(modsDirectory, fileName);
            progress?.Report(new InstallProgress($"正在下载 {fileName}", 40));

            using var download = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            download.EnsureSuccessStatusCode();

            // 字节级进度：以前只报 40% 和 100%，中间全程没有进度，分类进度条看着就是卡住的
            var total = download.Content.Headers.ContentLength ?? 0;
            var buffer = new byte[81920];
            long written = 0;
            var lastReport = DateTime.UtcNow;
            using (var source = await download.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var target = File.Create(targetPath))
            {
                int read;
                while ((read = await NetCompat.ReadAsyncTask(source, buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;

                    var now = DateTime.UtcNow;
                    if ((now - lastReport).TotalMilliseconds < 200 && (total <= 0 || written < total))
                    {
                        continue;
                    }

                    lastReport = now;
                    var percent = total > 0 ? (int)NetCompat.Clamp(written * 100.0 / total, 0, 99) : 40;
                    progress?.Report(new InstallProgress(
                        $"正在下载 {fileName}",
                        percent,
                        $"当前文件：{fileName}",
                        fileName,
                        written,
                        total));
                }
            }

            progress?.Report(new InstallProgress("模组下载完成", 100));
            var suffix = string.IsNullOrWhiteSpace(versionNumber) ? string.Empty : $"（{versionNumber}）";
            return ModInstallOutcome.Ok(targetPath, $"已下载 {fileName}{suffix} → {modsDirectory}");
        }
        catch (Exception ex)
        {
            return ModInstallOutcome.Fail($"模组下载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 加载器名 → Modrinth 的 categories slug。**大小写不敏感**，认不出来返回空串
    /// （调用方必须判空，别拼 "categories:"，见 SearchAsync 里的说明）。
    /// </summary>
    private static string LoaderSlug(string? loader) => loader?.Trim().ToLowerInvariant() switch
    {
        "forge" => "forge",
        "fabric" => "fabric",
        "neoforge" => "neoforge",
        "quilt" => "quilt",
        _ => string.Empty,
    };

    /// <summary>按项目 slug 取可用版本号（如 Iris 的 "iris"），用于"光影加载器"的版本下拉。</summary>
    public async Task<IReadOnlyList<string>> GetProjectVersionsAsync(
        string projectSlugOrId,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var loaderSlug = LoaderSlug(loader);
            var url = $"https://api.modrinth.com/v2/project/{projectSlugOrId}/version";
            var query = new List<string>();
            if (!string.IsNullOrWhiteSpace(gameVersion))
            {
                query.Add($"game_versions={Uri.EscapeDataString($"[\"{gameVersion}\"]")}");
            }

            if (!string.IsNullOrWhiteSpace(loaderSlug))
            {
                query.Add($"loaders={Uri.EscapeDataString($"[\"{loaderSlug}\"]")}");
            }

            if (query.Count > 0)
            {
                url += "?" + string.Join("&", query);
            }

            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement.EnumerateArray()
                .Select(v => GetString(v, "version_number"))
                .Where(v => v.Length > 0)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// 下载 Iris（光影加载器）到目标版本的 mods 目录。
    /// Iris 依赖 Sodium（缺了游戏会直接报 "Incompatible mods found" 起不来），所以顺带一起下。
    /// </summary>
    public async Task<ModInstallOutcome> DownloadIrisAsync(
        string gameVersion,
        string loader,
        string modsDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var iris = await DownloadAsync("iris", gameVersion, loader, modsDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
        if (!iris.Success)
        {
            return iris;
        }

        // Sodium 是 Iris 的硬依赖：下不到时给出明确提示，避免装完直接闪退
        var sodium = await DownloadAsync("sodium", gameVersion, loader, modsDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
        return sodium.Success
            ? ModInstallOutcome.Ok(iris.FilePath ?? sodium.FilePath ?? modsDirectory,
                $"{iris.Message} 并已安装依赖 Sodium。")
            : ModInstallOutcome.Fail($"{iris.Message}\n但 Iris 依赖的 Sodium 下载失败：{sodium.Message}");
    }

    /// <summary>
    /// 下载 OptiFine：从 optifine.net 的下载页找到匹配游戏版本的 jar，再走 adloadx → downloadx 取真实文件。
    /// 取到后放到目标版本目录，由用户双击运行一次完成注入（OptiFine 官方安装器没有可用的命令行模式）。
    /// </summary>
    public async Task<ModInstallOutcome> DownloadOptiFineAsync(
        string gameVersion,
        string targetDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report(new InstallProgress("正在查询 OptiFine 版本", 10));
            var page = await Http.GetStringAsync("https://optifine.net/downloads", cancellationToken).ConfigureAwait(false);
            var matches = System.Text.RegularExpressions.Regex.Matches(
                page,
                @"adloadx\?f=(OptiFine_" + System.Text.RegularExpressions.Regex.Escape(gameVersion) + @"_[A-Za-z0-9_.]+\.jar)");
            if (matches.Count == 0)
            {
                return ModInstallOutcome.Fail($"OptiFine 官网没有找到适配 {gameVersion} 的版本（可去 optifine.net 手动下载）。");
            }

            var fileName = matches[0].Groups[1].Value;
            progress?.Report(new InstallProgress($"正在解析 {fileName} 的下载地址", 35));
            var adPage = await Http.GetStringAsync($"https://optifine.net/adloadx?f={fileName}", cancellationToken)
                .ConfigureAwait(false);
            var token = System.Text.RegularExpressions.Regex.Match(adPage, @"downloadx\?f=[^""']*?&(?:amp;)?x=([A-Za-z0-9]+)");
            if (!token.Success)
            {
                return ModInstallOutcome.Fail("OptiFine 下载页需要人工确认（赞助页），请到 optifine.net 手动下载。");
            }

            var downloadUrl = $"https://optifine.net/downloadx?f={fileName}&x={token.Groups[1].Value}";
            Directory.CreateDirectory(targetDirectory);
            var targetPath = Path.Combine(targetDirectory, fileName);
            progress?.Report(new InstallProgress($"正在下载 {fileName}", 60));

            using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var destination = File.Create(targetPath))
            {
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new InstallProgress("OptiFine 下载完成", 100));
            return ModInstallOutcome.Ok(targetPath,
                $"已下载 {fileName} → {targetDirectory}。OptiFine 需要手动运行一次安装器完成注入。");
        }
        catch (Exception ex)
        {
            return ModInstallOutcome.Fail($"OptiFine 下载失败：{ex.Message}");
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
