using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>并发预下载的结果。</summary>
public sealed record GameFilesResult(bool Success, int Downloaded, int Skipped, int Failed, string Message);

/// <summary>
/// 并发下载某个版本运行所需的文件（版本 JSON / 客户端 jar / 库 / 资源索引 / 全部资源对象）。
///
/// 为什么要自己下一个：StarLight 是**串行**下载，遇到大文件（例如 1.13.2 的 realms 有 6.1 MB）时
/// 后面的文件全得排队等它；而且它每次请求都新建 HttpClient、没有超时，个别连接挂住就整轮卡死。
/// 这里改成多文件并发（默认 8 个并行）、每个文件都有超时与"官方 ↔ BMCLAPI"双源重试，
/// 下完再交给 StarLight 做收尾（它发现文件都在就很快过一遍），顺带给出真实的字节级进度。
/// </summary>
public sealed class GameFileDownloader
{
    private const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    /// <summary>BMCLAPI 镜像根地址（Forge 安装器等也要拼 maven 路径，所以放到 internal）。</summary>
    internal const string MirrorBase = "https://bmclapi2.bangbang93.com/";

    private const string OfficialLibraryBase = "https://libraries.minecraft.net/";
    private const string OfficialAssetBase = "https://resources.download.minecraft.net/";

    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromMinutes(2);

    /// <summary>等响应头的上限：BMCLAPI 被限流时会"连上但不回任何数据"，不能无限等。</summary>
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    /// <summary>两次读到数据之间的上限（同一个源）。</summary>
    private static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(30);

    /// <summary>单个源下载一个文件的总预算下限（按文件大小还会放宽）。</summary>
    private static readonly TimeSpan MinimumAttemptBudget = TimeSpan.FromSeconds(90);

    /// <summary>算总预算用的最低速度：10 MB 的文件给 400 秒，够慢的线路用，也能挡住"滴流"。</summary>
    private const long MinimumThroughputBytesPerSecond = 25 * 1024;

    private static string? _cachedManifest;

    internal sealed record PlannedFile(string Path, string OfficialUrl, string MirrorUrl, long Size);

    /// <summary>
    /// 两个下载源的健康度（一次安装内共享）。
    /// 某个源连续失败到阈值就先不用它——BMCLAPI 限流时是"连上不回数据"，不熔断的话每个文件都要先白等 30 秒。
    /// 只要有任意一次成功就清零，所以偶发抖动不会被误判。
    /// </summary>
    internal sealed class DownloadSourceHealth
    {
        private const int FailureThreshold = 5;

        private int _mirrorFailures;
        private int _officialFailures;

        public bool MirrorHealthy => Volatile.Read(ref _mirrorFailures) < FailureThreshold;

        public bool OfficialHealthy => Volatile.Read(ref _officialFailures) < FailureThreshold;

        /// <summary>某个源被熔断过（用于给用户一句提示）。</summary>
        public bool AnyTripped => !MirrorHealthy || !OfficialHealthy;

        public string Describe()
        {
            var parts = new List<string>();
            if (!MirrorHealthy)
            {
                parts.Add("BMCLAPI 镜像连续失败，已改用官方源");
            }

            if (!OfficialHealthy)
            {
                parts.Add("官方源连续失败，已改用 BMCLAPI 镜像");
            }

            return string.Join("；", parts);
        }

        public void Record(bool isMirror, bool success)
        {
            if (success)
            {
                if (isMirror)
                {
                    Interlocked.Exchange(ref _mirrorFailures, 0);
                }
                else
                {
                    Interlocked.Exchange(ref _officialFailures, 0);
                }

                return;
            }

            if (isMirror)
            {
                Interlocked.Increment(ref _mirrorFailures);
            }
            else
            {
                Interlocked.Increment(ref _officialFailures);
            }
        }
    }

    public async Task<GameFilesResult> DownloadAsync(
        string gameVersion,
        string gameRoot,
        bool useMirror,
        int concurrency = 8,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = PerFileTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.1 (github.com/Themeda/WorldBirdLauncher)");
        var health = new DownloadSourceHealth();

        try
        {
            var versionJsonPath = Path.Combine(gameRoot, "versions", gameVersion, gameVersion + ".json");
            if (!File.Exists(versionJsonPath))
            {
                progress?.Report(new InstallProgress("正在获取版本索引文件", -1, "正在准备下载清单…"));
                var json = await GetVersionJsonAsync(http, gameVersion, useMirror, cancellationToken).ConfigureAwait(false);
                if (json is null)
                {
                    return new GameFilesResult(false, 0, 0, 0, $"无法获取 {gameVersion} 的版本 JSON。");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(versionJsonPath)!);
                await NetCompat.WriteAllTextAsync(versionJsonPath, json, new System.Text.UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }

            var planned = BuildPlan(gameRoot, gameVersion, versionJsonPath, out var assetIndexId);

            // 资源索引本身要先有，才能列出全部资源对象
            if (assetIndexId is not null)
            {
                var indexFile = planned.FirstOrDefault(f =>
                    f.Path.EndsWith(Path.Combine("assets", "indexes", assetIndexId + ".json"), StringComparison.OrdinalIgnoreCase));
                if (indexFile is not null && !File.Exists(indexFile.Path))
                {
                    await TryDownloadAsync(http, indexFile, useMirror, null, cancellationToken, health).ConfigureAwait(false);
                }

                var indexJson = File.Exists(Path.Combine(gameRoot, "assets", "indexes", assetIndexId + ".json"))
                    ? await NetCompat.ReadAllTextAsync(Path.Combine(gameRoot, "assets", "indexes", assetIndexId + ".json"), cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                if (indexJson is not null)
                {
                    planned.AddRange(BuildAssetObjects(gameRoot, indexJson));
                }
            }

            return await DownloadPlannedAsync(http, health, planned, gameRoot, useMirror, concurrency, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GameFilesResult(false, 0, 0, 0, $"预下载失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按调用方自己拼好的下载清单下载（Forge 安装器用它下安装包内的库；可以不含资源对象）。
    /// 进度、并发、双源与熔断逻辑与 <see cref="DownloadAsync"/> 共用。
    /// </summary>
    internal async Task<GameFilesResult> DownloadPlannedAsync(
        IReadOnlyList<PlannedFile> planned,
        string gameRoot,
        bool useMirror,
        int concurrency = 8,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = PerFileTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.1 (github.com/Themeda/WorldBirdLauncher)");
        return await DownloadPlannedAsync(
                http, new DownloadSourceHealth(), planned, gameRoot, useMirror, concurrency, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<GameFilesResult> DownloadPlannedAsync(
        HttpClient http,
        DownloadSourceHealth health,
        IReadOnlyList<PlannedFile> planned,
        string gameRoot,
        bool useMirror,
        int concurrency,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var todo = planned.Where(f => !IsUpToDate(f)).ToList();
            var skipped = planned.Count - todo.Count;
            if (todo.Count == 0)
            {
                return new GameFilesResult(true, 0, skipped, 0, "所需文件已全部存在。");
            }

            var totalBytes = todo.Sum(f => Math.Max(0, f.Size));
            var doneBytes = 0L;
            var downloaded = 0;
            var failed = new List<string>();
            var active = new ConcurrentDictionary<string, (long Done, long Total)>();

            // 速度 / 剩余时间：按约 1 秒采样一次再指数平滑（瞬时值抖得太厉害，界面会一直跳）。
            // 大文件下载时"百分比几乎不动"最容易让人以为卡死，所以要多给这两个数字。
            var sampleClock = System.Diagnostics.Stopwatch.StartNew();
            var lastSampleBytes = 0L;
            var speedBytesPerSecond = 0d;

            using var reporterCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var reporter = Task.Run(async () =>
            {
                while (!reporterCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(400, reporterCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    var current = active.OrderByDescending(kv => kv.Value.Total).FirstOrDefault();
                    var nowBytes = Interlocked.Read(ref doneBytes);
                    var sampleSeconds = sampleClock.Elapsed.TotalSeconds;
                    if (sampleSeconds >= 1d)
                    {
                        var instant = (nowBytes - lastSampleBytes) / sampleSeconds;
                        speedBytesPerSecond = speedBytesPerSecond <= 0
                            ? instant
                            : speedBytesPerSecond * 0.6 + instant * 0.4;
                        lastSampleBytes = nowBytes;
                        sampleClock.Restart();
                    }

                    var percent = totalBytes > 0
                        ? (int)NetCompat.Clamp(nowBytes * 100.0 / totalBytes, 0, 100)
                        : (int)NetCompat.Clamp(downloaded * 100.0 / todo.Count, 0, 100);
                    var remaining = speedBytesPerSecond > 1024 && totalBytes > nowBytes
                        ? TimeSpan.FromSeconds((totalBytes - nowBytes) / speedBytesPerSecond)
                        : (TimeSpan?)null;
                    progress?.Report(new InstallProgress(
                        $"下载中 · {todo.Count - downloaded} 个文件待下载（{downloaded}/{todo.Count} 个文件）",
                        percent,
                        current.Key is null ? "正在准备下载清单…" : $"当前文件：{current.Key}",
                        current.Key,
                        current.Value.Done,
                        current.Value.Total,
                        speedBytesPerSecond,
                        remaining));
                }
            }, reporterCts.Token);

            using var gate = new SemaphoreSlim(NetCompat.Clamp(concurrency, 1, 16));
            var tasks = todo.Select(async file =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var relative = NetCompat.GetRelativePath(gameRoot, file.Path);
                    active[relative] = (0, Math.Max(0, file.Size));
                    if (await TryDownloadAsync(http, file, useMirror, (done, total) => active[relative] = (done, total), cancellationToken, health)
                            .ConfigureAwait(false))
                    {
                        Interlocked.Add(ref doneBytes, Math.Max(0, file.Size));
                        Interlocked.Increment(ref downloaded);
                    }
                    else
                    {
                        lock (failed)
                        {
                            failed.Add(relative);
                        }
                    }

                    active.TryRemove(relative, out _);
                }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            await reporterCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await reporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 忽略
            }

            progress?.Report(new InstallProgress(
                $"下载完成（{downloaded}/{todo.Count} 个文件）",
                100,
                failed.Count == 0 ? "所有文件已就绪" : $"有 {failed.Count} 个文件未能下载"));

            var tripped = health.AnyTripped ? $"（{health.Describe()}）" : string.Empty;
            return new GameFilesResult(
                failed.Count == 0,
                downloaded,
                skipped,
                failed.Count,
                failed.Count == 0
                    ? $"已并发下载 {downloaded} 个文件（跳过 {skipped} 个已存在的）。{tripped}"
                    : $"有 {failed.Count} 个文件未下载成功，交给后续流程重试：{string.Join("、", failed.Take(5))}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GameFilesResult(false, 0, 0, 0, $"预下载失败：{ex.Message}");
        }
    }

    private static bool IsUpToDate(PlannedFile file)
    {
        try
        {
            var info = new FileInfo(file.Path);
            return info.Exists && (file.Size <= 0 || info.Length == file.Size);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task<bool> TryDownloadAsync(
        HttpClient http,
        PlannedFile file,
        bool useMirror,
        Action<long, long>? onProgress,
        CancellationToken cancellationToken,
        DownloadSourceHealth? health = null)
    {
        var candidates = new List<(string? Url, bool IsMirror)>();
        if (useMirror)
        {
            candidates.Add((file.MirrorUrl, true));
            candidates.Add((file.OfficialUrl, false));
        }
        else
        {
            candidates.Add((file.OfficialUrl, false));
            candidates.Add((file.MirrorUrl, true));
        }

        // 熔断：某个源连续失败若干次就本次安装不再用它（BMCLAPI 限流时能省掉每个文件 30 秒的白等）。
        // 两个源都不健康时还是按原顺序照试，避免把自己锁死。
        if (health is not null)
        {
            var healthy = candidates
                .Where(c => c.IsMirror ? health.MirrorHealthy : health.OfficialHealthy)
                .ToList();
            if (healthy.Count > 0)
            {
                candidates = healthy;
            }
        }

        foreach (var (url, isMirror) in candidates)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var temporary = file.Path + ".part";
            HttpResponseMessage? response = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);

                using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    headerCts.CancelAfter(HeaderTimeout);
                    response = await http
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                        .ConfigureAwait(false);
                }

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                {
                    continue;
                }

                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? file.Size;

                // 单个源的总预算：按"最低速度"给（大文件多给点时间），到点就换源，不再无限等
                var budget = TimeSpan.FromSeconds(Math.Max(
                    MinimumAttemptBudget.TotalSeconds,
                    file.Size > 0 ? file.Size / (double)MinimumThroughputBytesPerSecond : 0));
                using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budgetCts.CancelAfter(budget);

                var buffer = new byte[81920];
                long written = 0;
                using (var source = await response.Content.ReadAsStreamAsync(budgetCts.Token).ConfigureAwait(false))
                using (var target = File.Create(temporary))
                {
                    // 单文件"停滞保护"：镜像偶尔会给一个慢到 1KB/s 都不到的连接。
                    // HttpClient.Timeout 对"响应头已到、随后持续滴流"的连接不一定生效，
                    // 实测会出现一个文件挂十几分钟、整轮安装看起来卡死。这里给每次读取加 30 秒上限，
                    // 超时即放弃本 URL、换下一个源重试。
                    while (true)
                    {
                        // 注意：有些网络环境下 socket 读**根本不理会取消令牌**（BMCLAPI 被限流时实测就是
                        // "连上、0 字节、十分钟不动"）。所以这里用"超时竞速 + 直接 Dispose 掉响应"来强制拆连接，
                        // 光靠 CancellationToken 是不够的。
                        var readTask = NetCompat.ReadAsyncTask(source, buffer, budgetCts.Token);
                        var winner = await Task.WhenAny(readTask, Task.Delay(NoDataTimeout, CancellationToken.None))
                            .ConfigureAwait(false);
                        if (winner != readTask)
                        {
                            _ = readTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                            response.Dispose();
                            response = null;
                            throw new IOException(
                                $"当前源 {NoDataTimeout.TotalSeconds:0} 秒没有返回数据（疑似被限流/挂住），换源重试。");
                        }

                        var read = await readTask.ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        written += read;
                        onProgress?.Invoke(written, total);
                    }
                }

                NetCompat.MoveFile(temporary, file.Path, overwrite: true);
                health?.Record(isMirror, success: true);
                return true;
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception)
                {
                    // 清理失败不影响换源重试
                }

                health?.Record(isMirror, success: false);
            }
            finally
            {
                response?.Dispose();
            }
        }

        return false;
    }

    private static async Task<string?> GetVersionJsonAsync(HttpClient http, string gameVersion, bool useMirror, CancellationToken cancellationToken)
    {
        var mirrorUrl = $"{MirrorBase}version/{Uri.EscapeDataString(gameVersion)}/json";
        if (useMirror)
        {
            try
            {
                return await http.GetStringAsync(mirrorUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 换官方
            }
        }

        try
        {
            _cachedManifest ??= await http.GetStringAsync(OfficialManifest, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(_cachedManifest);
            if (document.RootElement.TryGetProperty("versions", out var versions))
            {
                foreach (var item in versions.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    if (!string.Equals(id, gameVersion, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var url = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception)
        {
            // 落到镜像再试一次
        }

        try
        {
            return await http.GetStringAsync(mirrorUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static List<PlannedFile> BuildPlan(string gameRoot, string gameVersion, string versionJsonPath, out string? assetIndexId)
    {
        var planned = new List<PlannedFile>();
        assetIndexId = null;
        using var document = JsonDocument.Parse(File.ReadAllText(versionJsonPath));
        var root = document.RootElement;
        var versionDirectory = Path.Combine(gameRoot, "versions", gameVersion);

        if (root.TryGetProperty("downloads", out var downloads))
        {
            if (downloads.TryGetProperty("client", out var client))
            {
                var url = client.TryGetProperty("url", out var clientUrl) ? clientUrl.GetString() : null;
                var size = client.TryGetProperty("size", out var clientSize) && clientSize.TryGetInt64(out var value) ? value : 0;
                planned.Add(new PlannedFile(
                    Path.Combine(versionDirectory, gameVersion + ".jar"),
                    url ?? string.Empty,
                    $"{MirrorBase}version/{Uri.EscapeDataString(gameVersion)}/client",
                    size));
            }

        }

        // 资源索引：官方版本 JSON 是**顶层** `assetIndex`（不是 `downloads.assetIndex`，
        // 早期实现只看 downloads 里，等于永远拿不到索引与资源对象，装完的游戏没有贴图/声音）。
        // 这里两种位置都认，优先顶层。
        if ((root.TryGetProperty("assetIndex", out var assetIndex) ||
             (downloads.ValueKind == JsonValueKind.Object && downloads.TryGetProperty("assetIndex", out assetIndex))) &&
            assetIndex.ValueKind == JsonValueKind.Object)
        {
            var id = assetIndex.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            assetIndexId = id;
            if (!string.IsNullOrWhiteSpace(id))
            {
                planned.Add(new PlannedFile(
                    Path.Combine(gameRoot, "assets", "indexes", id + ".json"),
                    assetIndex.TryGetProperty("url", out var indexUrl) ? indexUrl.GetString() ?? string.Empty : string.Empty,
                    $"{MirrorBase}assets/indexes/{id}.json",
                    assetIndex.TryGetProperty("size", out var indexSize) && indexSize.TryGetInt64(out var indexValue) ? indexValue : 0));
            }
        }

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!MinecraftLibraryRules.IsAllowedForThisSystem(library))
                {
                    continue;
                }

                // 一个库可能要两个文件：主 artifact + 当前系统的 natives 包（1.16.x/1.17.x 的
                // 官方 JSON 就是靠"同名两条"来表达这件事，natives 包缺了启动会报 lwjgl.dll 找不到）
                var plannedAny = false;
                foreach (var (relativePath, fileUrl, _, fileSize) in MinecraftLibraryRules.ResolveFiles(library))
                {
                    plannedAny = true;
                    planned.Add(new PlannedFile(
                        Path.Combine(gameRoot, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar)),
                        string.IsNullOrWhiteSpace(fileUrl) ? OfficialLibraryBase + relativePath : fileUrl!,
                        MirrorBase + "maven/" + relativePath,
                        fileSize));
                }

                if (plannedAny)
                {
                    continue;
                }

                // 有 downloads 节点、但里面既没有可用 artifact 也没有 classifiers：
                // 这是 Mojang 早已下架的包（1.7.10 的 tv.twitch:twitch-platform / twitch-external-platform 就是
                // `path`/`url` 全空的这种），官方源上已经没有文件了，直接跳过——
                // 千万别拿 maven 坐标去拼一个必然 404 的地址，否则整个安装会被它拖失败（实测踩过）。
                if (library.TryGetProperty("downloads", out var downloadsNode) &&
                    downloadsNode.ValueKind == JsonValueKind.Object)
                {
                    continue;
                }

                // 加载器（Fabric / Forge）的版本 JSON 是 Maven 风格：只有 name + url + size，没有 downloads 节点
                var name = library.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var baseUrl = library.TryGetProperty("url", out var baseUrlElement) ? baseUrlElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || ParseCoordinates(name!) is not { } maven)
                {
                    continue;
                }

                var relative = $"{maven.Group.Replace('.', '/')}/{maven.Artifact}/{maven.Version}/{maven.Artifact}-{maven.Version}.jar";
                var size = library.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeValue) ? sizeValue : 0;

                // 没写 url 的（1.7.10-Forge 这类老版本的 6 个库就是）按官方安装器的做法回落到 libraries.minecraft.net，
                // 否则这些库会从下载清单里消失，启动时报 NoClassDefFoundError（实测踩过）。
                var officialBase = string.IsNullOrWhiteSpace(baseUrl) ? OfficialLibraryBase : baseUrl!;
                planned.Add(new PlannedFile(
                    Path.Combine(gameRoot, "libraries", relative.Replace('/', Path.DirectorySeparatorChar)),
                    officialBase.TrimEnd('/') + "/" + relative,
                    MirrorBase + "maven/" + relative,
                    size));
            }
        }

        return planned;
    }

    private static (string Group, string Artifact, string Version)? ParseCoordinates(string name)
    {
        var parts = name.Split(':');
        return parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : null;
    }

    private static IEnumerable<PlannedFile> BuildAssetObjects(string gameRoot, string indexJson)
    {
        using var document = JsonDocument.Parse(indexJson);
        if (!document.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in objects.EnumerateObject())
        {
            var hash = property.Value.TryGetProperty("hash", out var hashElement) ? hashElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(hash) || hash!.Length < 2)
            {
                continue;
            }

            var size = property.Value.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : 0;
            var prefix = hash[..2];
            yield return new PlannedFile(
                Path.Combine(gameRoot, "assets", "objects", prefix, hash),
                OfficialAssetBase + prefix + "/" + hash,
                MirrorBase + "assets/" + prefix + "/" + hash,
                size);
        }
    }
}
