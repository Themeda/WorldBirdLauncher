using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// Java 运行时管理。
///
/// 说明：检测与下载/解压/校验**全部由本项目自研**（不再依赖 StarLight 的 JavaUtil），
/// 下载源使用 Adoptium（Eclipse Temurin）官方 API。
/// </summary>
public sealed class JavaRuntimeService
{
    private const string AdoptiumApiBase =
        "https://api.adoptium.net/v3/assets/latest/{0}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public JavaRuntimeService(string? runtimeRoot = null)
    {
        RuntimeRoot = runtimeRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WorldBirdLauncher", "runtime");

        var parent = Path.GetDirectoryName(RuntimeRoot.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        JavaScanCachePath = Path.Combine(
            string.IsNullOrEmpty(parent) ? RuntimeRoot : parent, "java-scan.json");
    }

    /// <summary>Java 运行时安装根目录。</summary>
    public string RuntimeRoot { get; }

    /// <summary>
    /// 「深度扫描记忆」文件：上次深度扫描找到的 javaw.exe 路径。
    /// 快扫会把这里记下的路径也算作候选——用户点过一次「深度扫描」之后，
    /// 藏在 `D:\java` 这类自定义目录里的 Java 不会在下次启动时"消失"。
    /// </summary>
    public string JavaScanCachePath { get; }

    /// <summary>深度扫描的时间预算（到点就停，先把已找到的返回）。</summary>
    public TimeSpan DeepScanBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 检测本机已安装的 Java（自研，不再依赖 StarLight 的 JavaUtil）。
    ///
    /// `deepSearch = false`（默认）：**快扫**——注册表、JAVA_HOME/PATH、常见安装目录、官方启动器运行时，
    /// 外加 <see cref="JavaScanCachePath">上次深度扫描的记忆</see>，通常几百毫秒到几秒出结果。
    /// `deepSearch = true`：快扫结束后再做**深度扫描**（遍历固定/可移动磁盘，跳过系统与缓存目录），
    /// 受 <see cref="DeepScanBudget"/>（默认 30 秒）与 <paramref name="cancellationToken"/> 限制。
    /// </summary>
    public IReadOnlyList<JavaRuntime> Detect(
        bool deepSearch = false,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        onProgress?.Invoke("正在扫描常见安装位置（注册表 / 环境变量 / 常见目录）…");
        foreach (var exe in FastCandidates())
        {
            candidates.Add(exe);
        }

        foreach (var exe in CachedJavaExecutables())
        {
            candidates.Add(exe);
        }

        if (deepSearch)
        {
            foreach (var exe in DeepCandidates(onProgress, cancellationToken))
            {
                candidates.Add(exe);
            }

            SaveScanCache(candidates);
        }

        var result = new List<JavaRuntime>();
        foreach (var exe in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SafeFileExists(exe))
            {
                continue;
            }

            JavaRuntime? runtime;
            try
            {
                runtime = ReadJavaRuntime(exe);
            }
            catch (Exception)
            {
                // 读不了（权限/文件损坏）就当这个候选不存在
                continue;
            }

            if (runtime is null)
            {
                continue;
            }

            result.Add(runtime);
            onProgress?.Invoke($"找到 Java {runtime.MajorVersion}：{exe}");
        }

        return result
            .GroupBy(r => r.JavaPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.MajorVersion)
            .ThenBy(r => r.JavaPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>快扫候选：注册表 + 环境变量 + 常见安装目录 + 官方启动器运行时。</summary>
    private IEnumerable<string> FastCandidates()
    {
        foreach (var home in RegistryJavaHomes())
        {
            foreach (var exe in ScanForJavaExecutables(home, maxDepth: 3))
            {
                yield return exe;
            }
        }

        foreach (var home in new[]
                 {
                     Environment.GetEnvironmentVariable("JAVA_HOME"),
                     Environment.GetEnvironmentVariable("JDK_HOME"),
                 })
        {
            if (string.IsNullOrWhiteSpace(home))
            {
                continue;
            }

            foreach (var exe in ScanForJavaExecutables(home!, maxDepth: 2))
            {
                yield return exe;
            }
        }

        // PATH / Oracle javapath 这类"只放了 java.exe 的目录"
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (JavaExecutableIn(trimmed) is { } onPath)
            {
                yield return onPath;
            }
        }

        foreach (var root in SupplementarySearchRoots().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var exe in ScanForJavaExecutables(root, maxDepth: 4))
            {
                yield return exe;
            }
        }
    }

    /// <summary>
    /// 上次深度扫描记住的 Java 路径（仍然存在的才算）。用于让快扫"记得"自定义目录里的 Java。
    /// </summary>
    private IEnumerable<string> CachedJavaExecutables()
    {
        foreach (var path in ReadScanCachePaths())
        {
            if (!string.IsNullOrWhiteSpace(path) && SafeFileExists(path))
            {
                yield return path;
            }
        }
    }

    /// <summary>把这次扫描的候选写回「深度扫描记忆」（与旧记录合并，掉文件的条目自动丢掉）。</summary>
    private void SaveScanCache(IEnumerable<string> candidates)
    {
        try
        {
            var merged = ReadScanCachePaths()
                .Concat(candidates)
                .Where(path => !string.IsNullOrWhiteSpace(path) && SafeFileExists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var directory = Path.GetDirectoryName(JavaScanCachePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                JavaScanCachePath,
                JsonSerializer.Serialize(
                    new JavaScanCache
                    {
                        UpdatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                        Paths = merged,
                    },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // 记忆文件写不进去不影响本次检测结果
        }
    }

    private List<string> ReadScanCachePaths()
    {
        try
        {
            if (!SafeFileExists(JavaScanCachePath))
            {
                return [];
            }

            var cache = JsonSerializer.Deserialize<JavaScanCache>(File.ReadAllText(JavaScanCachePath));
            return cache?.Paths is { Count: > 0 } paths ? paths : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>「深度扫描记忆」文件的形状（见 <see cref="JavaScanCachePath"/>）。</summary>
    private sealed class JavaScanCache
    {
        public string UpdatedUtc { get; set; } = string.Empty;

        public List<string> Paths { get; set; } = [];
    }

    /// <summary>
    /// 深度扫描：**先扫各用户目录**（`.p2`、`.jdks`、`scoop`、`AppData\Local\Programs` 这些藏 Java 的地方，
    /// 实测 StarLight 找到的那台就藏在另一个用户的 Eclipse `.p2\pool\plugins` 里），再**各磁盘并行**遍历。
    /// 所有任务共用一个截止时间（<see cref="DeepScanBudget"/>，默认 30 秒）+ 取消令牌。
    /// </summary>
    private IEnumerable<string> DeepCandidates(Action<string>? onProgress, CancellationToken cancellationToken)
    {
        var state = new DeepScanState(onProgress);
        var deadline = DateTimeOffset.UtcNow + DeepScanBudget;
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 注意：**绝不能**把 bin / lib 这类目录加进来——javaw.exe 就在 <JavaHome>\bin 里，
            // 剪掉 bin 等于一个 Java 都扫不到（2026-10-06 实测漏检 D:\java\bin\javaw.exe 就是这个原因）。
            "$RECYCLE.BIN", "System Volume Information", "Windows", "ProgramData", "node_modules",
            ".git", ".gradle", ".m2", "obj", "$WinREAgent", "Recovery", "Temp", "Caches",
        };

        // 阶段 A：用户目录（更深，最多 8 层）— 阶段 B：各磁盘（最多 9 层）
        var roots = new List<(string Root, int MaxDepth)>();
        foreach (var usersRoot in UserProfileRoots())
        {
            roots.Add((usersRoot, 8));
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            roots.Add((drive.RootDirectory.FullName, 9));
        }

        if (roots.Count == 0)
        {
            yield break;
        }

        onProgress?.Invoke($"开始深度扫描：{roots.Count} 个根目录并行，最多 {DeepScanBudget.TotalSeconds:0} 秒（可取消）…");

        var startedAt = DateTimeOffset.UtcNow;
        var tasks = new List<Task>();
        foreach (var (root, maxDepth) in roots)
        {
            var scanRoot = root;
            var depth = maxDepth;
            tasks.Add(Task.Run(
                () => ScanTree(scanRoot, depth, deadline, cancellationToken, skipped, state),
                CancellationToken.None));
        }

        // 等所有扫描任务收尾（各自到点/取消即返回，最多再等 5 秒兜底）
        try
        {
            Task.WaitAll([.. tasks], DeepScanBudget + TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // 扫描任务内部已吞掉异常；这里只是兜底
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (cancellationToken.IsCancellationRequested)
        {
            onProgress?.Invoke($"深度扫描已取消：用时 {elapsed.TotalSeconds:0.0} 秒，已扫描 {state.DirectoryCount} 个目录，找到 {state.Found.Count} 个 Java。");
        }
        else
        {
            var suffix = DateTimeOffset.UtcNow >= deadline && tasks.Any(t => !t.IsCompleted)
                ? "（到达时间预算，其余的没扫完）"
                : string.Empty;
            onProgress?.Invoke(
                $"深度扫描结束：用时 {elapsed.TotalSeconds:0.0} 秒，扫描 {state.DirectoryCount} 个目录，找到 {state.Found.Count} 个 Java{suffix}。");
        }

        foreach (var exe in state.Found)
        {
            yield return exe;
        }
    }

    /// <summary>需要深入扫描的用户目录：当前用户 + 其它用户的用户目录（Java 常藏在个人目录里）。</summary>
    private static IEnumerable<string> UserProfileRoots()
    {
        var current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            yield return current;
        }

        var usersRoot = Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\", "Users");
        if (!Directory.Exists(usersRoot))
        {
            yield break;
        }

        IEnumerable<string> profiles;
        try
        {
            profiles = Directory.EnumerateDirectories(usersRoot);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var profile in profiles)
        {
            if (string.Equals(profile, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return profile;
        }
    }

    /// <summary>
    /// 广度优先扫一棵目录树，把找到的 javaw.exe/java.exe 丢进结果袋；到点或取消就返回。
    /// 单棵树出任何异常都只丢这一棵，不影响其它根目录。
    /// </summary>
    private void ScanTree(
        string root,
        int maxDepth,
        DateTimeOffset deadline,
        CancellationToken cancellationToken,
        HashSet<string> skipped,
        DeepScanState state)
    {
        try
        {
            var pending = new Queue<(string Directory, int Depth)>();
            pending.Enqueue((root, 0));
            while (pending.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested || DateTimeOffset.UtcNow >= deadline)
                {
                    return;
                }

                var (directory, depth) = pending.Dequeue();
                state.OnDirectoryScanned(directory);

                if (JavaExecutableIn(directory) is { } executable)
                {
                    state.Found.Add(executable);
                }

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var subdirectory in EnumerateDirectoriesSafe(directory))
                {
                    var leaf = Path.GetFileName(subdirectory);
                    if (string.IsNullOrEmpty(leaf) ||
                        skipped.Contains(leaf) ||
                        subdirectory.StartsWith(RuntimeRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pending.Enqueue((subdirectory, depth + 1));
                }
            }
        }
        catch (Exception)
        {
            // 权限/路径过长等问题：丢掉这一棵树，不影响其它根目录
        }
    }

    private static IEnumerable<string> JavaExecutableNames()
    {
        yield return "javaw.exe";
        yield return "java.exe";
    }

    /// <summary>
    /// 目录里的 Java 启动器：**优先 javaw.exe**（不会弹控制台窗口），没有才退回 java.exe。
    /// 同一个目录只返回一个，避免 D:\java\bin 这种同时有 java.exe / javaw.exe 的目录被算成两个 Java。
    /// </summary>
    private static string? JavaExecutableIn(string directory)
    {
        foreach (var name in JavaExecutableNames())
        {
            var candidate = Path.Combine(directory, name);
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 路径非法/权限问题：跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 安全地列出一个目录的直接子目录。
    /// **必须像这样先物化成 List**：<c>Directory.EnumerateDirectories</c> 是惰性的，
    /// 把 `try/catch` 套在"拿到枚举器"那一句上根本抓不到异常（真正的读取发生在 foreach 的 MoveNext 里），
    /// 结果就是"某个子目录无权访问" → 整棵树直接中断（2026-10-06 实测深扫只跑几秒就结束的原因之一）。
    /// </summary>
    private static List<string> EnumerateDirectoriesSafe(string directory)
    {
        var list = new List<string>();
        try
        {
            foreach (var subdirectory in Directory.EnumerateDirectories(directory))
            {
                list.Add(subdirectory);
            }
        }
        catch (Exception)
        {
            // 读不了就返回已经读到的部分
        }

        return list;
    }

    /// <summary>
    /// 深度扫描的状态袋：并行任务共用一个「已扫描目录数」与「命中的 javaw.exe」，
    /// 每扫过 <see cref="ReportEvery"/> 个目录给 UI 报一次进度。
    /// </summary>
    private sealed class DeepScanState
    {
        private const int ReportEvery = 400;

        private readonly Action<string>? _onProgress;
        private long _directories;

        public DeepScanState(Action<string>? onProgress) => _onProgress = onProgress;

        public System.Collections.Concurrent.ConcurrentBag<string> Found { get; } = new();

        public int DirectoryCount => (int)Interlocked.Read(ref _directories);

        public void OnDirectoryScanned(string directory)
        {
            var count = Interlocked.Increment(ref _directories);
            if (count % ReportEvery == 0)
            {
                _onProgress?.Invoke(
                    $"深度扫描中：已扫描 {count} 个目录，找到 {Found.Count} 个 Java…（当前：{directory}）");
            }
        }
    }

    /// <summary>注册表里登记的 JavaHome（32/64 位视图 + 当前用户）。</summary>
    private static IEnumerable<string> RegistryJavaHomes()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hiveName in new[] { "SOFTWARE\\JavaSoft\\Java Runtime Environment", "SOFTWARE\\JavaSoft\\JDK", "SOFTWARE\\JavaSoft\\Java Development Kit" })
            {
                foreach (var baseKey in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    var home = TryReadRegistryJavaHome(baseKey, view, hiveName);
                    if (home.Length > 0)
                    {
                        yield return home;
                    }
                }
            }
        }
    }

    private static string TryReadRegistryJavaHome(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null)
            {
                return string.Empty;
            }

            foreach (var versionName in key.GetSubKeyNames())
            {
                using var versionKey = key.OpenSubKey(versionName);
                if (versionKey?.GetValue("JavaHome") is string home && !string.IsNullOrWhiteSpace(home))
                {
                    return home;
                }
            }
        }
        catch (Exception)
        {
            // 注册表读不了就算了
        }

        return string.Empty;
    }

    /// <summary>
    /// 读一个 javaw.exe/java.exe 的版本信息：优先读同目录的 <c>release</c> 文件（免启动进程），
    /// 读不到才跑一次 <c>java -XshowSettings:properties -version</c> 兜底。
    /// </summary>
    private static JavaRuntime? ReadJavaRuntime(string javaExe)
    {
        var binDirectory = Path.GetDirectoryName(javaExe) ?? string.Empty;
        var home = Path.GetDirectoryName(binDirectory) ?? binDirectory;

        var version = string.Empty;
        var arch = string.Empty;
        var releasePath = Path.Combine(home, "release");
        if (File.Exists(releasePath))
        {
            foreach (var line in File.ReadAllLines(releasePath))
            {
                if (line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal))
                {
                    version = line["JAVA_VERSION=".Length..].Trim().Trim('"');
                }
                else if (line.StartsWith("OS_ARCH=", StringComparison.Ordinal))
                {
                    arch = line["OS_ARCH=".Length..].Trim().Trim('"');
                }
            }
        }

        if (version.Length == 0 && !TryProbeVersion(javaExe, out version, out arch))
        {
            return null;
        }

        return new JavaRuntime(
            javaExe,
            binDirectory,
            ParseMajorVersion(version),
            version,
            !arch.Contains("x86", StringComparison.OrdinalIgnoreCase) || arch.Contains("64", StringComparison.Ordinal));
    }

    /// <summary>跑一次 java -XshowSettings:properties -version 兜底读版本（3 秒超时）。</summary>
    private static bool TryProbeVersion(string javaExe, out string version, out string arch)
    {
        version = string.Empty;
        arch = string.Empty;

        // javaw.exe 不输出控制台内容，换成同目录的 java.exe
        var probe = Path.Combine(Path.GetDirectoryName(javaExe) ?? string.Empty, "java.exe");
        if (!File.Exists(probe))
        {
            probe = javaExe;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = probe,
                Arguments = "-XshowSettings:properties -version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                    // 忽略
                }

                return false;
            }

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("java.version =", StringComparison.Ordinal))
                {
                    version = trimmed["java.version =".Length..].Trim();
                }
                else if (trimmed.StartsWith("sun.arch.data.model =", StringComparison.Ordinal))
                {
                    arch = trimmed["sun.arch.data.model =".Length..].Trim();
                }
            }

            return version.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>版本号 → 主版本：1.8.0_281 → 8；17.0.9 → 17；25 → 25。</summary>
    private static int ParseMajorVersion(string version)
    {
        var parts = version.Split('.', '_', '-');
        if (parts.Length == 0 || !int.TryParse(parts[0], out var first))
        {
            return 0;
        }

        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy))
        {
            return legacy;
        }

        return first;
    }

    /// <summary>
    /// 除 StarLight 自带搜寻范围外，还需要额外扫描的 Java 根目录。
    /// 顺序按命中概率排列：本项目运行时 → 官方启动器运行时 → 常见 JDK 安装位置。
    /// </summary>
    private IEnumerable<string> SupplementarySearchRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return RuntimeRoot;
        yield return Path.Combine(appData, ".minecraft", "runtime");
        yield return Path.Combine(localAppData, ".minecraft", "runtime");
        yield return Path.Combine(localAppData, "Programs", "Eclipse Adoptium");
        yield return Path.Combine(programFiles, "Eclipse Adoptium");
        yield return Path.Combine(programFilesX86, "Eclipse Adoptium");
        yield return Path.Combine(programFiles, "Java");
        yield return Path.Combine(programFilesX86, "Java");
        yield return Path.Combine(programFiles, "Microsoft");
        yield return Path.Combine(programFiles, "Amazon Corretto");
        yield return Path.Combine(programFiles, "Zulu");
        yield return Path.Combine(programFiles, "BellSoft");
        yield return Path.Combine(programFiles, "AdoptOpenJDK");
        yield return Path.Combine(programFiles, "Semeru");
        yield return Path.Combine(userProfile, ".jdks");
    }

    /// <summary>
    /// 在指定根目录下按广度优先、限定层数地查找 Java 启动器（避免整盘遍历太慢）。
    /// 快扫与"手动指定目录扫描"共用它；抛出的异常不会外泄（读不了的目录直接跳过）。
    /// </summary>
    internal static IReadOnlyList<string> ScanForJavaExecutables(string root, int maxDepth, CancellationToken cancellationToken = default)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !SafeDirectoryExists(root))
        {
            return found;
        }

        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var (directory, depth) = pending.Dequeue();
            if (JavaExecutableIn(directory) is { } executable)
            {
                found.Add(executable);
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var subdirectory in EnumerateDirectoriesSafe(directory))
            {
                pending.Enqueue((subdirectory, depth + 1));
            }
        }

        return found;
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 这个游戏版本能不能用"比它声明的更高的 Java"启动。
    ///
    /// **1.13 之前（1.12.2 / 1.7.10 这些，LWJGL2 那代）不行**：Java 9+ 会因为反射/模块访问直接崩，
    /// 必须用 Java 8。1.13 起换成 LWJGL3，用更高的 Java 一般都能跑（1.18.2 用 Java 21 实测正常），
    /// 所以不必非要再下一份"刚好那版"。
    ///
    /// 版本号解析不出来（自定义版本名）时按"现代版本"处理，保持宽松。
    /// </summary>
    public static bool AllowsNewerJava(string gameVersionId) =>
        !VersionCompatibility.TryParseVersionKey(gameVersionId, out var key) || key >= 1.13;

    /// <summary>
    /// 从本机已有的 Java 里挑一个最合适的（挑不到返回 null，调用方据此提示 / 下载）：
    /// ① 正好等于声明版本的优先；
    /// ② 没有就用"≥ 声明版本"里**最小**的那个（1.13+ 允许更高的 Java，不必再下 200 MB）；
    /// ③ 老版本（&lt; 1.13）只接受 Java 8 —— 没有 Java 8 就返回 null，让调用方提示/下载 Java 8。
    /// </summary>
    public static JavaRuntime? PickBest(IReadOnlyList<JavaRuntime> runtimes, string gameVersionId, int requiredMajor)
    {
        if (runtimes.Count == 0)
        {
            return null;
        }

        if (!AllowsNewerJava(gameVersionId))
        {
            return runtimes
                .Where(j => j.MajorVersion == 8)
                .OrderByDescending(j => j.Is64Bit)
                .ThenBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return runtimes
            .Where(j => j.MajorVersion >= requiredMajor)
            .OrderBy(j => j.MajorVersion == requiredMajor ? 0 : 1) // 完全一致的最优先
            .ThenBy(j => j.MajorVersion)                            // 其次取最接近（最小）的
            .ThenByDescending(j => j.Is64Bit)
            .ThenBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>按游戏版本推荐 Java 主版本号。</summary>
    public static int RecommendFeatureVersion(string minecraftVersion)
    {
        var (major, minor, patch) = ParseVersion(minecraftVersion);
        if (major <= 0)
        {
            return 21;
        }

        // 26.x 及以后的新版本号方案
        if (major > 1)
        {
            return 21;
        }

        return minor switch
        {
            <= 16 => 8,
            17 => 17,
            18 or 19 => 17,
            20 => patch >= 5 ? 21 : 17,
            _ => 21,
        };
    }

    /// <summary>
    /// 启动某个游戏版本真正需要的 Java 主版本：优先读取该版本 JSON 里声明的
    /// <c>javaVersion.majorVersion</c>（例如 26.1 声明 25），读不到时再退回
    /// <see cref="RecommendFeatureVersion"/> 的启发式规则。
    ///
    /// 说明：硬编码"高版本 = 21"会误判——26.x 的版本 JSON 声明的是 25，
    /// 且其 JVM 参数含只有较新 JDK 才认识的选项，用 21 启动会直接报"无法创建 Java 虚拟机"。
    /// </summary>
    public static int RequiredFeatureVersion(string gameRoot, string versionId) =>
        ReadDeclaredJavaMajorVersion(gameRoot, versionId) ?? RecommendFeatureVersion(versionId);

    /// <summary>
    /// 从游戏版本 JSON 读取声明的 Java 主版本，读不到返回 <c>null</c>。
    /// 找不到 <c>javaVersion</c> 时会沿 <c>inheritsFrom</c> 向上追溯（Fabric / Forge 等继承版本）。
    /// </summary>
    public static int? ReadDeclaredJavaMajorVersion(string gameRoot, string versionId)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        var currentId = versionId.Trim();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var hop = 0; hop < 5 && !string.IsNullOrWhiteSpace(currentId); hop++)
        {
            if (!visited.Add(currentId!))
            {
                break;
            }

            var jsonPath = Path.Combine(gameRoot, "versions", currentId, currentId + ".json");
            if (!File.Exists(jsonPath))
            {
                return null;
            }

            string json;
            try
            {
                json = File.ReadAllText(jsonPath);
            }
            catch (Exception)
            {
                return null;
            }

            var declared = ParseDeclaredJavaMajorVersion(json);
            if (declared is not null)
            {
                return declared;
            }

            currentId = ReadInheritsFrom(json);
        }

        return null;
    }

    /// <summary>解析单份版本 JSON 中声明的 Java 主版本号；未声明或格式非法时返回 <c>null</c>。</summary>
    public static int? ParseDeclaredJavaMajorVersion(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadJavaMajorVersion(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadJavaMajorVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("javaVersion", out var javaVersion) ||
            javaVersion.ValueKind != JsonValueKind.Object ||
            !javaVersion.TryGetProperty("majorVersion", out var major))
        {
            return null;
        }

        return major.ValueKind switch
        {
            JsonValueKind.Number when major.TryGetInt32(out var value) && value > 0 => value,
            JsonValueKind.String when int.TryParse(major.GetString(), out var parsed) && parsed > 0 => parsed,
            _ => null,
        };
    }

    private static string? ReadInheritsFrom(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("inheritsFrom", out var inherited) &&
                inherited.ValueKind == JsonValueKind.String)
            {
                return inherited.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>查询指定主版本号最新的 JDK 下载信息。</summary>
    public async Task<JavaDownloadInfo> ResolveAsync(int featureVersion, CancellationToken cancellationToken = default)
    {
        var url = string.Format(AdoptiumApiBase, featureVersion);
        var json = await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"未找到 Java {featureVersion} 的下载信息。");
        }

        var first = root[0];
        var releaseName = first.TryGetProperty("release_name", out var rn) ? rn.GetString() ?? string.Empty : string.Empty;
        var package = first.GetProperty("binary").GetProperty("package");

        return new JavaDownloadInfo(
            featureVersion,
            releaseName,
            package.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
            package.GetProperty("link").GetString() ?? string.Empty,
            package.TryGetProperty("checksum", out var c) ? c.GetString() : null,
            package.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
    }

    /// <summary>下载并解压指定主版本号的 JDK，返回可用的 Java 运行时。</summary>
    public async Task<JavaRuntime> InstallAsync(
        int featureVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new InstallProgress($"查询 Java {featureVersion} 下载地址", 0));
        var download = await ResolveAsync(featureVersion, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(RuntimeRoot);
        var targetDirectory = Path.Combine(RuntimeRoot, $"jdk-{featureVersion}");
        var zipPath = Path.Combine(RuntimeRoot, download.FileName);

        progress?.Report(new InstallProgress($"下载 {download.FileName}（{download.SizeMb} MB）", 0));
        await DownloadAsync(download, zipPath, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress("正在解压 Java", 95));
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        NetCompat.ExtractToDirectory(zipPath, targetDirectory, overwriteFiles: true);

        var javaExe = Directory.EnumerateFiles(targetDirectory, "javaw.exe", SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException("解压后未找到 javaw.exe，下载的压缩包可能不完整。");

        try
        {
            File.Delete(zipPath);
        }
        catch (IOException)
        {
            // 压缩包被占用时忽略，不影响使用
        }

        var runtime = ReadJavaRuntime(javaExe)
                      ?? throw new InvalidOperationException("无法读取下载的 Java 版本信息。");

        progress?.Report(new InstallProgress("Java 安装完成", 100));
        return runtime;
    }

    private static async Task DownloadAsync(
        JavaDownloadInfo download,
        string targetPath,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Http
            .GetAsync(download.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? download.SizeBytes;
        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = File.Create(targetPath);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await NetCompat.ReadAsyncTask(source, buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total > 0)
            {
                var percent = (int)NetCompat.Clamp(received * 90 / total, 0, 90);
                progress?.Report(new InstallProgress(
                    $"下载 Java {Math.Round(received / 1024d / 1024d, 1)} / {Math.Round(total / 1024d / 1024d, 1)} MB",
                    percent));
            }
        }

        if (!string.IsNullOrWhiteSpace(download.Sha256))
        {
            var actual = NetCompat.ToHexString(
                NetCompat.Sha256(await NetCompat.ReadAllBytesAsync(targetPath, cancellationToken)
                    .ConfigureAwait(false)));
            if (!string.Equals(actual, download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Java 安装包校验失败（SHA-256 不匹配），已中止安装。");
            }
        }
    }

    private static (int Major, int Minor, int Patch) ParseVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return (0, 0, 0);
        }

        var parts = version.Trim().Split('.', '-', '+');
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (int.TryParse(part, out var value))
            {
                numbers.Add(value);
            }

            if (numbers.Count == 3)
            {
                break;
            }
        }

        return (numbers.Count > 0 ? numbers[0] : 0,
                numbers.Count > 1 ? numbers[1] : 0,
                numbers.Count > 2 ? numbers[2] : 0);
    }
}
