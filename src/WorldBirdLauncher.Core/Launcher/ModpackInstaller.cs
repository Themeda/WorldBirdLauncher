using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// 整合包处理：识别格式 → 解析元数据 → 安装加载器 → 落盘文件的完整流程。
///
/// 支持四种格式：
/// <list type="bullet">
/// <item>离线完整包（压缩包内直接是 .minecraft 结构）</item>
/// <item>CurseForge（manifest.json + overrides/）</item>
/// <item>Modrinth（modrinth.index.json + overrides/）</item>
/// <item>MultiMC / Prism（mmc-pack.json + instance.cfg）</item>
/// </list>
/// 注意：CurseForge 的模组文件需要通过 CurseForge API 解析下载地址，未配置 API Key 时
/// 这些文件会被记为"未能下载"，其余内容（加载器、配置、本人打包进 overrides 的文件）照常安装。
/// </summary>
public sealed class ModpackInstaller
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly GameInstallationService _installation;
    private readonly ICurseForgeUrlResolver? _curseForgeResolver;

    public ModpackInstaller(GameInstallationService installation, ICurseForgeUrlResolver? curseForgeResolver = null)
    {
        _installation = installation;
        _curseForgeResolver = curseForgeResolver;
    }

    /// <summary>只解析整合包信息，不做任何安装动作（界面预览用）。</summary>
    public ModpackInfo Read(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = archive.Entries
            .Where(e => e.Length > 0 && !IsNoise(e.FullName))
            .ToList();

        if (TryFind(entries, "manifest.json", out var curseEntry))
        {
            return ParseCurseForge(archive, curseEntry!, entries, archivePath);
        }

        if (entries.FirstOrDefault(e => Path.GetFileName(e.FullName).Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase))
            is { } modrinthEntry)
        {
            return ParseModrinth(archive, modrinthEntry, entries, archivePath);
        }

        if (TryFind(entries, "mmc-pack.json", out _))
        {
            return ParseMultiMc(archive, entries, archivePath);
        }

        return ParseOfflineArchive(entries, archivePath);
    }

    /// <summary>安装整合包。</summary>
    public async Task<ModpackInstallResult> InstallAsync(
        string archivePath,
        string gameRoot,
        string? instanceId = null,
        string? curseForgeApiKey = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool allowVersionDownload = true)
    {
        var info = Read(archivePath);

        // 阉割版（2026-10-20 用户确认）：不下载游戏版本 → 整合包必须自带游戏本体
        if (!allowVersionDownload &&
            (info.Format != ModpackFormat.OfflineArchive ||
             string.IsNullOrWhiteSpace(info.MinecraftVersion) ||
             !ArchiveContainsGameBody(archivePath, info.MinecraftVersion!)))
        {
            return new ModpackInstallResult
            {
                Success = false,
                Message = "该整合包不包含游戏本体（只有清单 / overrides），本版本不下载游戏版本，无法解析。\n" +
                          "请使用自带 .minecraft 游戏本体的离线整合包，或先用官方启动器安装对应游戏版本。",
            };
        }

        if (string.IsNullOrWhiteSpace(info.MinecraftVersion))
        {
            return new ModpackInstallResult
            {
                Success = false,
                Message = "无法从整合包中识别出 Minecraft 版本，无法安装。",
            };
        }

        // CurseForge 清单里只有 projectID/fileID、没有下载地址 → 通过服务端代理批量换地址
        // （客户端不内置 API Key，先查服务端缓存，命中就不消耗 CurseForge 配额）。
        if (info.Format == ModpackFormat.CurseForge
            && info.RemoteFiles.Any(f => f.IsUnresolved)
            && _curseForgeResolver is not null)
        {
            info = await ResolveCurseForgeFilesAsync(info, progress, cancellationToken).ConfigureAwait(false);
        }

        // ★ 整合包实例目录名：默认用整合包名。不能覆盖"不是我们建的"版本目录（同名版本/加载器已存在时换个名字）。
        var id = mcsl_MakeInstanceIdUnique(gameRoot, string.IsNullOrWhiteSpace(instanceId) ? SanitizeId(info.Name) : instanceId!);
        var mcVersion = info.MinecraftVersion!;

        var loaderVersionId = mcVersion;
        if (allowVersionDownload)
        {
            // 1. 基础版本
            progress?.Report(new InstallProgress($"检查基础版本 {mcVersion}", 2));
            if (!_installation.GetInstalledVersions(gameRoot)
                    .Where(v => v.IsUsable)
                    .Any(v => string.Equals(v.Id, mcVersion, StringComparison.OrdinalIgnoreCase)))
            {
                progress?.Report(new InstallProgress($"安装原版 {mcVersion}", 5));
                var vanilla = await _installation.InstallVanillaAsync(mcVersion, gameRoot, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!vanilla.Success)
                {
                    return new ModpackInstallResult { Success = false, Message = $"基础版本安装失败：{vanilla.Message}" };
                }
            }

            // 2. 加载器
            var loaderResult = await InstallLoaderAsync(info, mcVersion, gameRoot, progress, cancellationToken)
                .ConfigureAwait(false);
            if (loaderResult is { Success: false })
            {
                return new ModpackInstallResult { Success = false, Message = loaderResult.Message };
            }

            loaderVersionId = loaderResult?.VersionId is { Length: > 0 } loaderId ? loaderId : mcVersion;
        }
        else
        {
            // 不下载：直接用压缩包里自带的版本目录（解压后校验）
            progress?.Report(new InstallProgress($"使用整合包自带的游戏本体 {mcVersion}", 5));
        }

        // 3. 整合包实例：mods / overrides 都写进 versions\<实例名>，
        //    并且**必须**给这个目录写一份"继承加载器版本"的版本 JSON ——
        //    否则这个目录启动不了，启动器会去启动加载器目录（那里没有 mods），
        //    用户看到的是"安装完成但模组不生效"（2026-10-07 真机用 3 个整合包复现的 bug）。
        var targetDirectory = Path.Combine(gameRoot, "versions", id);
        Directory.CreateDirectory(targetDirectory);
        var launchVersionId = loaderVersionId;
        if (!string.Equals(id, loaderVersionId, StringComparison.OrdinalIgnoreCase))
        {
            mcsl_WriteInstanceJson(targetDirectory, id, loaderVersionId);

            // 模块化加载器（Forge 1.17+ / NeoForge）的 jvm 参数里有 -DignoreList=client-extra,${version_name}.jar，
            // 是**按文件名**把"客户端 jar"排除在模块层之外的；整合包实例的 version_name 是实例名，
            // 所以实例目录里也要有一份以实例名命名的 jar（内容沿用加载器目录那份），否则会拿原版 jar 当客户端 jar 崩掉。
            var loaderJar = Path.Combine(gameRoot, "versions", loaderVersionId, loaderVersionId + ".jar");
            var instanceJar = Path.Combine(targetDirectory, id + ".jar");
            if (File.Exists(loaderJar))
            {
                File.Copy(loaderJar, instanceJar, overwrite: true);
            }

            launchVersionId = id;
        }

        var failed = new List<string>();
        progress?.Report(new InstallProgress("解压整合包内容", 60));
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            foreach (var file in info.ArchiveFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(file.RelativePath);
                if (entry is null)
                {
                    failed.Add(file.RelativePath);
                    continue;
                }

                var destination = ResolveDestination(file.RelativePath, gameRoot, targetDirectory);
                if (destination is null)
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }
        }

        if (!allowVersionDownload)
        {
            var bundled = _installation.GetInstalledVersions(gameRoot)
                .FirstOrDefault(v => string.Equals(v.Id, loaderVersionId, StringComparison.OrdinalIgnoreCase));
            if (bundled is null || !bundled.IsUsable)
            {
                return new ModpackInstallResult
                {
                    Success = false,
                    Message = $"整合包内的游戏本体不完整（找不到可用的版本 {loaderVersionId}），" +
                              "本版本不下载游戏版本，无法补全。",
                };
            }
        }

        // 4. 下载远程文件：**边下边写**（先写 .part 再原子改名）+ 30 秒无数据看护 + 单文件进度/速度/剩余时间。
        //    以前是 ReadAsByteArrayAsync 一次性读进内存再落盘 —— 整个下载期间目标目录一个字节都不变，
        //    用户的"卡死看护"会把"正在慢慢下"误判成卡死，界面上也看不到任何进度（AGENTS.md 第 20/22 条）。
        var downloadable = info.RemoteFiles.Where(f => !f.IsFromArchive).ToList();
        if (downloadable.Count > 0)
        {
            var totalBytes = downloadable.Sum(f => Math.Max(0, f.SizeBytes));
            var doneBytes = 0L;
            var done = 0;
            foreach (var file in downloadable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveDestination(file.RelativePath, gameRoot, targetDirectory);
                if (destination is null)
                {
                    continue;
                }

                // 已经有完整文件（上次装到一半、或重复导入同一个包）就直接跳过，不重复下载
                if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                {
                    done++;
                    doneBytes += Math.Max(0, file.SizeBytes);
                    progress?.Report(new InstallProgress(
                        $"整合包文件已存在 {done}/{downloadable.Count}",
                        60 + (int)(35.0 * doneBytes / Math.Max(1, totalBytes)),
                        Path.GetFileName(file.RelativePath)));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(file.DownloadUrl)
                    || !file.DownloadUrl!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add(file.RelativePath);
                    continue;
                }

                var success = false;
                var skippedBytes = doneBytes;
                for (var attempt = 1; attempt <= 2 && !success; attempt++)
                {
                    try
                    {
                        doneBytes = await DownloadFileAsync(
                                file,
                                destination,
                                skippedBytes,
                                totalBytes,
                                done,
                                downloadable.Count,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);
                        success = true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        doneBytes = skippedBytes;
                        if (attempt == 2)
                        {
                            LogDownloadFailure(file.RelativePath, ex);
                            failed.Add(file.RelativePath);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                done++;
            }
        }

        progress?.Report(new InstallProgress("整合包安装完成", 100));
        var message = "整合包安装完成。";
        if (failed.Count > 0)
        {
            var examples = string.Join("、", failed.Take(3).Select(Path.GetFileName));
            message = $"整合包安装完成，但有 {failed.Count} 个文件没能下载（例如 {examples}"
                      + (failed.Count > 3 ? " 等" : string.Empty) + "）。";
            if (info.Format == ModpackFormat.CurseForge && string.IsNullOrWhiteSpace(curseForgeApiKey))
            {
                message += " CurseForge 格式的模组需要通过 CurseForge API 下载，请先在「设置」里填 API Key 再重新导入。";
            }
        }

        return new ModpackInstallResult
        {
            Success = true,
            Message = message,
            InstanceId = launchVersionId,
            SkippedFiles = failed.Count,
            FailedFiles = failed,
        };
    }

    /// <summary>
    /// 下载单个文件：先写 <c>&lt;目标&gt;.part</c>，成功后再改名成正式文件；
    /// 期间按 ~1 秒采样汇报"已下/总量 · 速度 · 剩余时间"，并且**30 秒收不到任何数据就掐掉这次连接**
    /// （踩过的坑：CDN 挂住的连接不理会取消令牌，必须 Dispose 掉 response 才能真正断开）。
    /// </summary>
    private async Task<long> DownloadFileAsync(
        ModpackFile file,
        string destination,
        long doneBytesBefore,
        long totalBytes,
        int done,
        int totalCount,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partPath = destination + ".part";

        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 用"数组元素"做共享状态：局部变量既被 lambda 捕获、又要传给 Interlocked（ref）会编译不过（CS1686）
        var activity = new long[] { DateTimeOffset.UtcNow.Ticks };
        using var watchdog = new System.Threading.Timer(
            _ =>
            {
                var silence = TimeSpan.FromTicks(
                    DateTimeOffset.UtcNow.Ticks - System.Threading.Interlocked.Read(ref activity[0]));
                if (silence >= TimeSpan.FromSeconds(30))
                {
                    watchdogCts.Cancel();
                }
            },
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5));

        using var response = await Http
            .GetAsync(file.DownloadUrl!, HttpCompletionOption.ResponseHeadersRead, watchdogCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var fileTotal = response.Content.Headers.ContentLength ?? file.SizeBytes;
        using var source = await response.Content.ReadAsStreamAsync(watchdogCts.Token).ConfigureAwait(false);
        using var target = File.Create(partPath);

        var buffer = new byte[81920];
        long fileDone = 0;
        var sampleClock = System.Diagnostics.Stopwatch.StartNew();
        var lastSampleBytes = 0L;
        var speed = 0d;

        while (true)
        {
            var read = await NetCompat.ReadAsyncTask(source, buffer, watchdogCts.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            target.Write(buffer, 0, read);
            fileDone += read;
            System.Threading.Interlocked.Exchange(ref activity[0], DateTimeOffset.UtcNow.Ticks);

            var seconds = sampleClock.Elapsed.TotalSeconds;
            if (seconds >= 1)
            {
                var instant = (fileDone - lastSampleBytes) / seconds;
                speed = speed <= 0 ? instant : speed * 0.6 + instant * 0.4;
                lastSampleBytes = fileDone;
                sampleClock.Restart();
            }

            if (progress is not null && (fileDone == read || speed > 0))
            {
                var remaining = speed > 1024 && fileTotal > fileDone
                    ? TimeSpan.FromSeconds((fileTotal - fileDone) / speed)
                    : (TimeSpan?)null;
                progress.Report(new InstallProgress(
                    $"下载整合包文件 {done + 1}/{totalCount}",
                    60 + (int)(35.0 * (doneBytesBefore + fileDone) / Math.Max(1, totalBytes)),
                    Path.GetFileName(file.RelativePath),
                    file.RelativePath,
                    fileDone,
                    fileTotal,
                    speed,
                    remaining));
            }
        }

        target.Flush();
        target.Dispose();

        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(partPath, destination);
        return doneBytesBefore + fileDone;
    }

    /// <summary>下载失败写诊断日志（客户端会把它带到 launcher-error.log 一起排查）。</summary>
    private static void LogDownloadFailure(string relativePath, Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorldBirdLauncher");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "launcher-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 整合包文件下载失败（{relativePath}）：{error.GetType().Name}: {error.Message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 记日志失败不影响安装流程
        }
    }

    /// <summary>
    /// 把 CurseForge 清单里"只有 projectID/fileID"的文件通过服务端代理换成真实下载地址；
    /// 能换到真实文件名的，顺手把路径里的占位文件名（`cf-&lt;项目&gt;-&lt;文件&gt;.jar`）也改掉。
    /// </summary>
    private async Task<ModpackInfo> ResolveCurseForgeFilesAsync(
        ModpackInfo info,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = info.RemoteFiles
            .Where(f => f.IsUnresolved)
            .Select(f => (File: f, Id: ParseCurseForgeId(f.RelativePath)))
            .Where(x => x.Id is not null)
            .ToList();
        if (pending.Count == 0 || _curseForgeResolver is null)
        {
            return info;
        }

        progress?.Report(new InstallProgress($"正在解析 CurseForge 下载地址（{pending.Count} 个文件）", 52));

        var map = await _curseForgeResolver
            .ResolveAsync(pending.Select(x => x.Id!.Value).ToList(), cancellationToken)
            .ConfigureAwait(false);

        if (map.Count == 0)
        {
            return info;
        }

        var resolvedFiles = info.RemoteFiles.Select(file =>
        {
            if (!file.IsUnresolved)
            {
                return file;
            }

            var id = ParseCurseForgeId(file.RelativePath);
            if (id is null || !map.TryGetValue($"{id.Value.ModId}/{id.Value.FileId}", out var resolved))
            {
                return file;
            }

            var directory = file.RelativePath.Contains('/')
                ? file.RelativePath[..(file.RelativePath.LastIndexOf('/') + 1)]
                : string.Empty;
            var name = string.IsNullOrWhiteSpace(resolved.Name) ? Path.GetFileName(file.RelativePath) : resolved.Name!;
            return file with { DownloadUrl = resolved.Url, RelativePath = directory + name };
        }).ToList();

        return info with { RemoteFiles = resolvedFiles };
    }

    /// <summary>从 `mods/cf-238222-6600311.jar` 里取回 CurseForge 的 (projectID, fileID)。</summary>
    private static (string ModId, string FileId)? ParseCurseForgeId(string relativePath)
    {
        var match = Regex.Match(Path.GetFileName(relativePath), @"^cf-(\d+)-(\d+)\.jar$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    /// <summary>
    /// 为整合包实例写一份可启动的版本 JSON：只声明 id 与 inheritsFrom，
    /// 其余（libraries / arguments / mainClass / 客户端 jar）都沿父版本继承。
    /// </summary>
    private static void mcsl_WriteInstanceJson(string directory, string instanceId, string parentId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["id"] = instanceId,
            ["inheritsFrom"] = parentId,
            ["type"] = "release",
            ["mcslModpack"] = true,
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, instanceId + ".json"), json);
    }

    /// <summary>
    /// 目标实例名被占用时换一个名字；如果那个目录是我们之前为整合包建的（JSON 里有 mcslModpack 标记），就复用。
    /// </summary>
    private static string mcsl_MakeInstanceIdUnique(string gameRoot, string baseId)
    {
        var candidate = baseId;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var jsonPath = Path.Combine(gameRoot, "versions", candidate, candidate + ".json");
            if (!File.Exists(jsonPath))
            {
                return candidate;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (document.RootElement.TryGetProperty("mcslModpack", out var flag) && flag.ValueKind == JsonValueKind.True)
                {
                    return candidate; // 上次导入这个整合包建的就是它
                }
            }
            catch (JsonException)
            {
                // 读不出来就当它不是我们的，换个名字
            }

            candidate = $"{baseId}-modpack{(attempt == 0 ? string.Empty : (attempt + 1).ToString())}";
        }

        return baseId + "-modpack";
    }

    private async Task<InstallOutcome?> InstallLoaderAsync(
        ModpackInfo info,
        string mcVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.Loader) || info.Loader is "原版")
        {
            return null;
        }

        progress?.Report(new InstallProgress($"安装 {info.Loader} 加载器", 25));

        if (info.Loader is "Fabric")
        {
            var loaderVersion = info.LoaderVersion;
            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                loaderVersion = (await _installation.GetFabricLoaderVersionsAsync(mcVersion, cancellationToken)
                    .ConfigureAwait(false)).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(loaderVersion))
            {
                return InstallOutcome.Fail($"未找到适用于 {mcVersion} 的 Fabric 加载器版本。");
            }

            return await _installation.InstallFabricAsync(mcVersion, loaderVersion!, gameRoot, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (info.Loader is "Forge")
        {
            var forgeVersion = info.LoaderVersion;
            if (string.IsNullOrWhiteSpace(forgeVersion))
            {
                forgeVersion = (await _installation.GetForgeVersionsAsync(mcVersion, cancellationToken)
                    .ConfigureAwait(false)).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(forgeVersion))
            {
                return InstallOutcome.Fail($"未找到适用于 {mcVersion} 的 Forge 版本。");
            }

            return await _installation.InstallForgeAsync(mcVersion, forgeVersion!, gameRoot, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (info.Loader is "NeoForge")
        {
            var neoVersion = info.LoaderVersion;
            if (string.IsNullOrWhiteSpace(neoVersion))
            {
                neoVersion = (await _installation.GetNeoForgeVersionsAsync(mcVersion, cancellationToken)
                    .ConfigureAwait(false)).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(neoVersion))
            {
                return InstallOutcome.Fail($"未找到适用于 {mcVersion} 的 NeoForge 版本。");
            }

            return await _installation.InstallWithFallbackAsync(
                    mcVersion, "NeoForge", neoVersion, gameRoot, _installation.UseMirror, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        if (info.Loader is "Quilt")
        {
            var quiltVersion = info.LoaderVersion;
            if (string.IsNullOrWhiteSpace(quiltVersion))
            {
                quiltVersion = (await _installation.GetQuiltLoaderVersionsAsync(mcVersion, cancellationToken)
                    .ConfigureAwait(false)).FirstOrDefault();
            }

            if (string.IsNullOrWhiteSpace(quiltVersion))
            {
                return InstallOutcome.Fail($"未找到适用于 {mcVersion} 的 Quilt 加载器版本。");
            }

            return await _installation.InstallWithFallbackAsync(
                    mcVersion, "Quilt", quiltVersion, gameRoot, _installation.UseMirror, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return InstallOutcome.Fail(
            $"暂不支持自动安装 {info.Loader} 加载器。可以先用官方安装器装好它，再重新导入整合包。");
    }

    /// <summary>把压缩包内的相对路径映射成磁盘目标路径；返回 null 表示跳过该文件。</summary>
    private static string? ResolveDestination(string entryPath, string gameRoot, string instanceDirectory)
    {
        var normalized = entryPath.Replace('/', Path.DirectorySeparatorChar);

        // 离线包内的 .minecraft/ 前缀 → 直接落到游戏根目录
        if (normalized.StartsWith($".minecraft{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(gameRoot, normalized[".minecraft".Length..].TrimStart(Path.DirectorySeparatorChar));
        }

        // CurseForge / Modrinth 的 overrides 前缀 → 落到版本隔离目录
        foreach (var prefix in new[] { "overrides", "client-overrides", "server-overrides" })
        {
            if (normalized.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized[(prefix.Length + 1)..];
                return Path.Combine(instanceDirectory, relative);
            }
        }

        // 其余情况统一落到版本隔离目录，避免污染其他版本的 mods/config
        return Path.Combine(instanceDirectory, normalized);
    }

    private static ModpackInfo ParseCurseForge(
        ZipArchive archive,
        ZipArchiveEntry manifestEntry,
        List<ZipArchiveEntry> entries,
        string archivePath)
    {
        using var stream = manifestEntry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? Path.GetFileNameWithoutExtension(archivePath)
            : Path.GetFileNameWithoutExtension(archivePath);

        string? mcVersion = null;
        string? loader = null;
        string? loaderVersion = null;
        if (root.TryGetProperty("minecraft", out var minecraft))
        {
            mcVersion = minecraft.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (minecraft.TryGetProperty("modLoaders", out var loaders) && loaders.GetArrayLength() > 0)
            {
                var primary = loaders.EnumerateArray()
                    .FirstOrDefault(l => l.TryGetProperty("primary", out var p) && p.GetBoolean());
                var id = primary.ValueKind == JsonValueKind.Undefined
                    ? loaders[0].GetProperty("id").GetString()
                    : primary.GetProperty("id").GetString();

                (loader, loaderVersion) = SplitLoaderId(id);
            }
        }

        var remote = new List<ModpackFile>();
        if (root.TryGetProperty("files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                var projectId = file.TryGetProperty("projectID", out var p) ? p.GetInt32() : 0;
                var fileId = file.TryGetProperty("fileID", out var f) ? f.GetInt32() : 0;
                remote.Add(new ModpackFile($"mods/cf-{projectId}-{fileId}.jar", null, 0));
            }
        }

        var overridesFolder = root.TryGetProperty("overrides", out var o)
            ? o.GetString() ?? "overrides"
            : "overrides";

        var local = entries
            .Where(e => e.FullName.StartsWith($"{overridesFolder}/", StringComparison.OrdinalIgnoreCase))
            .Select(e => new ModpackFile(e.FullName, null, e.Length, null, true))
            .ToList();

        return new ModpackInfo
        {
            Format = ModpackFormat.CurseForge,
            Name = name,
            MinecraftVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            RemoteFiles = remote,
            ArchiveFiles = local,
        };
    }

    private static ModpackInfo ParseModrinth(
        ZipArchive archive,
        ZipArchiveEntry indexEntry,
        List<ZipArchiveEntry> entries,
        string archivePath)
    {
        using var stream = indexEntry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? Path.GetFileNameWithoutExtension(archivePath)
            : Path.GetFileNameWithoutExtension(archivePath);

        string? mcVersion = null;
        string? loader = null;
        string? loaderVersion = null;
        if (root.TryGetProperty("dependencies", out var dependencies))
        {
            if (dependencies.TryGetProperty("minecraft", out var mc))
            {
                mcVersion = mc.GetString();
            }

            foreach (var (key, display) in new[]
                     {
                         ("fabric-loader", "Fabric"),
                         ("forge", "Forge"),
                         ("neoforge", "NeoForge"),
                         ("quilt-loader", "Quilt"),
                     })
            {
                if (dependencies.TryGetProperty(key, out var value))
                {
                    loader = display;
                    loaderVersion = value.GetString();
                    break;
                }
            }
        }

        var remote = new List<ModpackFile>();
        if (root.TryGetProperty("files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                var path = file.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                string? url = null;
                if (file.TryGetProperty("downloads", out var downloads) && downloads.GetArrayLength() > 0)
                {
                    url = downloads[0].GetString();
                }

                string? sha1 = null;
                if (file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("sha1", out var sha))
                {
                    sha1 = sha.GetString();
                }

                remote.Add(new ModpackFile(path!, url, file.TryGetProperty("fileSize", out var size) ? size.GetInt64() : 0, sha1));
            }
        }

        var local = entries
            .Where(e => e.FullName.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)
                        || e.FullName.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
            .Select(e => new ModpackFile(e.FullName, null, e.Length, null, true))
            .ToList();

        return new ModpackInfo
        {
            Format = ModpackFormat.Modrinth,
            Name = name,
            MinecraftVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            RemoteFiles = remote,
            ArchiveFiles = local,
        };
    }

    private static ModpackInfo ParseMultiMc(ZipArchive archive, List<ZipArchiveEntry> entries, string archivePath)
    {
        string? mcVersion = null;
        string? loader = null;
        string? loaderVersion = null;

        var packEntry = entries.FirstOrDefault(e =>
            Path.GetFileName(e.FullName).Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase));
        if (packEntry is not null)
        {
            using var stream = packEntry.Open();
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.TryGetProperty("components", out var components))
            {
                foreach (var component in components.EnumerateArray())
                {
                    var uid = component.TryGetProperty("uid", out var u) ? u.GetString() : null;
                    var version = component.TryGetProperty("version", out var v) ? v.GetString() : null;
                    switch (uid)
                    {
                        case "net.minecraft":
                            mcVersion = version;
                            break;
                        case "net.fabricmc.fabric-loader":
                            loader = "Fabric";
                            loaderVersion = version;
                            break;
                        case "net.minecraftforge":
                            loader = "Forge";
                            loaderVersion = version;
                            break;
                        case "net.neoforged":
                            loader = "NeoForge";
                            loaderVersion = version;
                            break;
                        case "org.quiltmc.quilt-loader":
                            loader = "Quilt";
                            loaderVersion = version;
                            break;
                    }
                }
            }
        }

        var local = entries
            .Where(e => !Path.GetFileName(e.FullName).Equals("mmc-pack.json", StringComparison.OrdinalIgnoreCase))
            .Select(e => new ModpackFile(e.FullName, null, e.Length, null, true))
            .ToList();

        return new ModpackInfo
        {
            Format = ModpackFormat.MultiMc,
            Name = Path.GetFileNameWithoutExtension(archivePath),
            MinecraftVersion = mcVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            ArchiveFiles = local,
        };
    }

    private static ModpackInfo ParseOfflineArchive(List<ZipArchiveEntry> entries, string archivePath)
    {
        // 尝试从 versions/<id>/<id>.json 推断版本号
        string? detected = null;
        foreach (var entry in entries)
        {
            var match = VersionJsonRegex.Match(entry.FullName);
            if (match.Success)
            {
                detected = match.Groups[1].Value;
                break;
            }
        }

        var local = entries
            .Select(e => new ModpackFile(e.FullName, null, e.Length, null, true))
            .ToList();

        return new ModpackInfo
        {
            Format = ModpackFormat.OfflineArchive,
            Name = Path.GetFileNameWithoutExtension(archivePath),
            MinecraftVersion = detected,
            Loader = "原版",
            ArchiveFiles = local,
        };
    }

    /// <summary>
    /// 整合包里到底有没有"能启动的游戏本体"：`versions/&lt;id&gt;/&lt;id&gt;.json` 必须有，
    /// 并且要么自带 `&lt;id&gt;.jar`，要么它的 inheritsFrom 父版本（最多追 3 层）在包里且带 jar。
    /// </summary>
    private static bool ArchiveContainsGameBody(string archivePath, string versionId)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return HasBundledVersion(archive, versionId, 0);
    }

    private static bool HasBundledVersion(ZipArchive archive, string versionId, int depth)
    {
        if (depth > 3 || string.IsNullOrWhiteSpace(versionId))
        {
            return false;
        }

        var id = Regex.Escape(versionId);
        var json = archive.Entries.FirstOrDefault(e =>
            Regex.IsMatch(e.FullName, $@"(^|/)versions/{id}/{id}\.json$", RegexOptions.IgnoreCase));
        if (json is null)
        {
            return false;
        }

        var jar = archive.Entries.FirstOrDefault(e =>
            Regex.IsMatch(e.FullName, $@"(^|/)versions/{id}/{id}\.jar$", RegexOptions.IgnoreCase));
        if (jar is not null)
        {
            return true;
        }

        // 加载器版本（Forge/NeoForge）常常只有 JSON，客户端 jar 沿 inheritsFrom 用父版本的
        try
        {
            using var stream = json.Open();
            using var document = JsonDocument.Parse(stream);
            var parent = document.RootElement.TryGetProperty("inheritsFrom", out var element)
                ? element.GetString()
                : null;
            return parent is not null && HasBundledVersion(archive, parent, depth + 1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (string? Loader, string? Version) SplitLoaderId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return (null, null);
        }

        var separator = id!.IndexOf('-');
        if (separator <= 0)
        {
            return (null, null);
        }

        var name = id[..separator].ToLowerInvariant();
        var version = id[(separator + 1)..];
        return name switch
        {
            "forge" => ("Forge", version),
            "fabric" => ("Fabric", version),
            "neoforge" => ("NeoForge", version),
            "quilt" => ("Quilt", version),
            _ => (null, null),
        };
    }

    private static bool TryFind(List<ZipArchiveEntry> entries, string fileName, out ZipArchiveEntry? entry)
    {
        entry = entries.FirstOrDefault(e =>
            Path.GetFileName(e.FullName).Equals(fileName, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    private static bool IsNoise(string path) =>
        path.EndsWith('/')
        || path.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);

    private static string SanitizeId(string name)
    {
        var cleaned = InvalidIdChars.Replace(name, "-").Trim('-', ' ');
        return string.IsNullOrWhiteSpace(cleaned) ? $"modpack-{DateTime.Now:yyyyMMddHHmm}" : cleaned;
    }

    // net48 上没有 [GeneratedRegex] 源生成器（System.Text.RegularExpressions 里也没那个特性），
    // 按本项目既有约定改成静态编译好的 Regex（阶段 1 在 Shared 里也是这么处理的）。
    private static readonly Regex VersionJsonRegex =
        new(@"versions/([^/]+)/\1\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InvalidIdChars =
        new(@"[\\/:*?""<>|]", RegexOptions.Compiled);
}
