using System.Windows.Threading;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Services;

/// <summary>一次完整安装的参数：游戏本体 + 加载器 + 光影 + 目标目录名。</summary>
public sealed record VersionInstallPlan(
    string GameVersion,
    string TargetName,
    string Loader,
    string? LoaderVersion,
    string Shader)
{
    public const string NoLoader = "不安装";
    public const string NoShader = "不安装";

    /// <summary>目标目录已存在且调用方已确认覆盖时置 true（"删除并重装"）。</summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// 安装一个可启动的版本：游戏本体 → 加载器（含依赖库）→ 光影（Iris/OptiFine）→ 目标目录名。
/// 「安装版本」弹窗与"直连启动发现版本不存在 → 自动下载"都走这里，避免两套实现漂移。
/// </summary>
public static class VersionInstallService
{
    public static async Task<(bool Success, string Message, string? VersionId)> InstallAsync(
        VersionInstallPlan plan,
        IReadOnlyList<DownloadStep> steps,
        IProgress<InstallProgress> progress,
        CancellationToken token)
    {
        var session = AppSession.Current;
        var mods = new ModDownloadService();
        var versionsRoot = Path.Combine(session.GameRoot, "versions");
        var installedId = plan.GameVersion;
        var warnings = new List<string>();

        // 安装进度看门狗：StarLight 只报阶段百分比（"下载游戏资源"会长时间停在 60%），
        // 这里改成按真实文件数量统计，并把 StarLight 的阶段文案当作阶段名透传。
        using var watcher = new InstallProgressWatcher(session.GameRoot, progress);
        var shim = new Progress<InstallProgress>(watcher.ReportExternal);
        watcher.Start("正在准备安装");

        try
        {
            var targetDirectory = Path.Combine(versionsRoot, plan.TargetName);
            if (plan.Overwrite && Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true); // 覆盖重装
            }

            // 1) 游戏本体
            var coreStep = steps.First(s => s.Category == DownloadCategory.游戏本体);
            coreStep.Status = DownloadStatus.下载中;
            if (!Directory.Exists(Path.Combine(versionsRoot, plan.GameVersion)))
            {
                var core = await session.Installation.InstallWithFallbackAsync(
                    plan.GameVersion, "原版", null, session.GameRoot, session.Settings.UseMirror, shim, token);
                if (!core.Success)
                {
                    return (false, $"游戏本体安装失败：{core.Message}", null);
                }

                installedId = string.IsNullOrWhiteSpace(core.VersionId) ? plan.GameVersion : core.VersionId!;
            }

            coreStep.Percentage = 100;
            coreStep.Status = DownloadStatus.已完成;

            // 2) 加载器
            if (plan.Loader != VersionInstallPlan.NoLoader)
            {
                var loaderStep = steps.First(s => s.Category == DownloadCategory.模组加载器);
                loaderStep.Status = DownloadStatus.下载中;
                var loaderResult = await session.Installation.InstallWithFallbackAsync(
                    plan.GameVersion, plan.Loader, plan.LoaderVersion, session.GameRoot, session.Settings.UseMirror, shim, token);
                if (!loaderResult.Success)
                {
                    return (false, $"{plan.Loader} 安装失败：{loaderResult.Message}", null);
                }

                installedId = string.IsNullOrWhiteSpace(loaderResult.VersionId) ? installedId : loaderResult.VersionId!;

                // 加载器自己的库（Fabric/Forge 的 Maven 风格依赖）也要下齐，否则启动会报 ClassNotFound
                await new GameFileDownloader().DownloadAsync(
                    installedId, session.GameRoot, session.Settings.UseMirror, 8, progress, token);
                loaderStep.Percentage = 100;
                loaderStep.Status = DownloadStatus.已完成;
            }

            // 3) 光影
            if (plan.Shader != VersionInstallPlan.NoShader)
            {
                var shaderStep = steps.First(s => s.Category == DownloadCategory.光影);
                shaderStep.Status = DownloadStatus.下载中;
                var versionDirectory = Path.Combine(versionsRoot, installedId);
                if (plan.Shader == "Iris")
                {
                    var irisLoader = plan.Loader is "Fabric" or "NeoForge" ? plan.Loader : "Fabric";
                    var irisResult = await mods.DownloadIrisAsync(
                        plan.GameVersion, irisLoader, Path.Combine(versionDirectory, "mods"), progress, token);
                    if (!irisResult.Success)
                    {
                        return (false, $"Iris 下载失败：{irisResult.Message}", null);
                    }
                }
                else if (plan.Shader == "OptiFine")
                {
                    // 安装器下载到独立目录（不再塞进版本目录），再用官方安装器的无界面入口自动装好
                    var installersDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "WorldBirdLauncher", "optifine-installers");
                    var optifineResult = await mods.DownloadOptiFineAsync(plan.GameVersion, installersDirectory, progress, token);
                    if (!optifineResult.Success || string.IsNullOrWhiteSpace(optifineResult.FilePath))
                    {
                        return (false, $"OptiFine 下载失败：{optifineResult.Message}", null);
                    }

                    var javaPath = ResolveJavaForOptiFine(session, plan.GameVersion);
                    if (javaPath is null)
                    {
                        return (false,
                            $"OptiFine 安装器已下载到 {installersDirectory}，但本机没有可用的 Java，无法自动安装。" +
                            "请手动运行该 jar 完成安装（装好后点设置页的「刷新版本列表」）。", null);
                    }

                    var optifineInstaller = new OptiFineInstallerService();
                    // OptiFine 打补丁没有进度回调：显示"不确定态 + 已用时"，避免看起来卡死
                    var optifineStarted = DateTime.Now;
                    watcher.SetOpaqueMode(true);
                    var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    ticker.Tick += (_, _) => watcher.ReportOpaqueStep(
                        "正在用 OptiFine 安装器注入（约 1-2 分钟）", DateTime.Now - optifineStarted);
                    ticker.Start();
                    OptiFineInstallOutcome install;
                    try
                    {
                        install = await optifineInstaller.InstallAsync(
                            session.GameRoot, javaPath, optifineResult.FilePath!, progress, token);
                    }
                    finally
                    {
                        ticker.Stop();
                        watcher.SetOpaqueMode(false);
                    }

                    if (!install.Success || string.IsNullOrWhiteSpace(install.VersionId))
                    {
                        return (false,
                            $"OptiFine 自动安装失败：{install.Message}\n" +
                            $"安装器已保留在 {installersDirectory}，可手动运行完成安装。", null);
                    }

                    installedId = install.VersionId;
                }

                shaderStep.Percentage = 100;
                shaderStep.Status = DownloadStatus.已完成;
            }

            // 4) 目标目录名
            if (!string.Equals(installedId, plan.TargetName, StringComparison.OrdinalIgnoreCase))
            {
                var finalName = plan.TargetName;
                if (Directory.Exists(Path.Combine(versionsRoot, finalName)))
                {
                    // 名字被占用（例如填了原版目录名、或那个名字的版本还在）：
                    // 不再让整个安装任务失败，自动换一个可用名并如实告知。
                    finalName = ResolveAvailableName(session.GameRoot, plan.TargetName);
                    warnings.Add($"「{plan.TargetName}」这个目录名已被占用");
                }

                if (!GameInstallationService.TryRenameVersion(session.GameRoot, installedId!, finalName, out var renameMessage))
                {
                    return (false, $"重命名失败：{renameMessage}", null);
                }

                installedId = finalName;
            }

            session.CurrentVersionId = installedId;
            var message = warnings.Count == 0
                ? $"安装完成：{installedId}"
                : $"安装完成：{installedId}（{string.Join("；", warnings)}，已自动装成这个名字）";
            return (true, message, installedId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>目录名被占用时找一个可用名：名字 (2) / 名字 (3) …</summary>
    public static string ResolveAvailableName(string gameRoot, string wanted)
    {
        var versionsRoot = Path.Combine(gameRoot, "versions");
        if (!Directory.Exists(Path.Combine(versionsRoot, wanted)))
        {
            return wanted;
        }

        for (var index = 2; index < 100; index++)
        {
            var candidate = $"{wanted} ({index})";
            if (!Directory.Exists(Path.Combine(versionsRoot, candidate)))
            {
                return candidate;
            }
        }

        return $"{wanted} ({DateTime.Now:HHmmss})";
    }

    /// <summary>OptiFine 安装器用的 Java：优先当前选择，其次本机满足版本要求里最接近的一个。</summary>
    private static string? ResolveJavaForOptiFine(AppSession session, string gameVersion)
    {
        if (session.CurrentJava is { } current && File.Exists(current.JavaPath))
        {
            return current.JavaPath;
        }

        var required = JavaRuntimeService.RequiredFeatureVersion(session.GameRoot, gameVersion);
        var runtimes = session.GetJavaRuntimes().Where(r => File.Exists(r.JavaPath)).ToList();
        // 与启动时同一套规则（完全一致的优先 → 其次"≥ 要求里最小的"；老版本只认 Java 8）
        return JavaRuntimeService.PickBest(runtimes, gameVersion, required)?.JavaPath
               ?? runtimes.OrderByDescending(r => r.MajorVersion).FirstOrDefault()?.JavaPath;
    }
}
