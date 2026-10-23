using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>启动参数构建结果（<paramref name="Segments"/> 按 StarLight 的四段结构：内存 / JVM / 窗口 / 游戏）。</summary>
public sealed record GameLaunchArguments(
    IReadOnlyList<string> Segments,
    string NativesDirectory,
    string ClientJarPath,
    string MainClass)
{
    /// <summary>构建过程中的非致命问题（例如外置登录的 authlib-injector 下载不到），调用方应展示给用户。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 自研启动参数构建，替代 StarLight.Core 的 <c>ArgumentsBuildUtil</c>。
///
/// 行为基线是 StarLight.Core 2.0.5（tag <c>v2.0.5</c>，`Utilities/ArgumentsBuildUtil.cs` + `Models/Utilities/BuildArgsData.cs`），
/// 刻意的差异只有这几处：
/// ① 不再用它的 <c>launch_wrapper.jar</c>（`-Doolloo.jlw.tmpdir=… -jar launch_wrapper.jar`），改成官方那种
///    `-cp &lt;类路径&gt; &lt;mainClass&gt;` 直接启动；
/// ② 品牌换成我们自己的（`-Dminecraft.launcher.brand` / `${launcher_name}` / `--versionType` 前缀），
///    对照回归时传 <c>"StarLight" / "2.0.5" / "SL"</c> 就能得到与 StarLight 逐字节相同的参数；
/// ③ 版本 JSON 里没被替换掉的占位符（实测 26.1 的 `${clientid}` / `${auth_xuid}`）不再原样塞给游戏，
///    而是连同它的选项一起丢掉；
/// ④ natives 目录：StarLight 在目录还不存在时会给出 `versions\&lt;id&gt;\natives`、却把文件解到
///    `versions\&lt;id&gt;\&lt;id&gt;-natives`（首启必报 Failed to locate library），我们统一用真正会解压的那个目录；
///    解压本身也只挑当前架构的 natives 包（StarLight 会把 arm64/x86 的 dll 一起盖进来）。
/// </summary>
public sealed class GameArgumentsBuilder : IDisposable
{
    private const string AuthlibLatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private const string Nide8AuthUrl = "https://login.mc-user.com:233/index/jar";

    /// <summary>登录注入器（authlib-injector / nide8auth）的下载上限：网络不通时不能把启动卡住。</summary>
    private static readonly TimeSpan AgentDownloadTimeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static readonly Regex OptionWithPlaceholder = new(@"--[A-Za-z][A-Za-z0-9-]*\s+\$\{[^}]+\}", RegexOptions.Compiled);
    private static readonly Regex LeftoverPlaceholder = new(@"\$\{[^}]+\}", RegexOptions.Compiled);
    private static readonly Regex RepeatedSpace = new(@"[ ]{2,}", RegexOptions.Compiled);

    /// <summary>老版本（只有 <c>minecraftArguments</c>）用的 JVM 模板——与 StarLight 的 <c>BuildArgsData.JvmArgumentsTemplate</c> 一致。</summary>
    private static readonly string[] DefaultJvmTemplate =
    [
        "-Djava.library.path=${natives_directory}",
        "-Dminecraft.launcher.brand=${launcher_name}",
        "-Dminecraft.launcher.version=${launcher_version}",
        "-cp",
        "${classpath}",
    ];

    private static readonly string[] DefaultGcArguments = ["-XX:+UseG1GC", "-XX:-UseAdaptiveSizePolicy"];

    private static readonly string[] DefaultAdvancedArguments =
    [
        "-XX:-OmitStackTraceInFastThrow",
        "-Dfml.ignoreInvalidMinecraftCertificates=true",
        "-Dfml.ignorePatchDiscrepancies=true",
        "-Dlog4j2.formatMsgNoLookups=true",
        "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump",
    ];

    private static readonly string[] OptimizationGcArguments =
    [
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:G1NewSizePercent=20",
        "-XX:G1ReservePercent=20",
        "-XX:MaxGCPauseMillis=50",
        "-XX:G1HeapRegionSize=16m",
    ];

    private static readonly string[] OptimizationAdvancedArguments =
    [
        "-XX:-DontCompileHugeMethods",
        "-Djava.rmi.server.useCodebaseOnly=true",
    ];

    private readonly string _root;
    private readonly string _versionId;
    private readonly MinecraftVersionChain _chain;

    public GameArgumentsBuilder(string gameRoot, string versionId)
    {
        _root = Path.GetFullPath(gameRoot);
        _versionId = versionId;
        _chain = MinecraftVersionChain.Load(_root, versionId);
        ClientJarPath = ResolveClientJarPath();
    }

    /// <summary>客户端 jar：有父版本时用父版本的 jar（Fabric/Forge 自己的目录里没有 jar），与 StarLight 一致。</summary>
    public string ClientJarPath { get; }

    /// <summary>
    /// 主类：沿版本链从"自己"往上找第一个声明了 <c>mainClass</c> 的版本。
    /// 整合包实例（`versions\&lt;包名&gt;\&lt;包名&gt;.json`）是"稀疏 JSON"，只写 id + inheritsFrom，
    /// 主类/参数都继承自加载器版本 —— 只读 Self 会得到空串，启动直接 `ClassNotFoundException`（实测踩过）。
    /// </summary>
    public string MainClass =>
        _chain.Entries.Select(e => e.MainClass).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? string.Empty;

    /// <summary>本项目的品牌/版本（写进 <c>-Dminecraft.launcher.*</c> 与 <c>--versionType</c>）。</summary>
    public static string DefaultLauncherVersion =>
        (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1, 0)).ToString(3);

    /// <summary>
    /// 构建启动参数。段结构与 StarLight 相同：内存段 / JVM 段 / 窗口段 / 游戏段，
    /// 调用方直接 <c>string.Join(" ", segments)</c> 即可；自动进服参数由调用方追加在最后。
    /// </summary>
    public async Task<GameLaunchArguments> BuildAsync(
        AccountProfile account,
        int minMemoryMb,
        int maxMemoryMb,
        int windowWidth,
        int windowHeight,
        bool fullScreen,
        bool isVersionIsolation = true,
        string launcherBrand = "WorldBirdLauncher",
        string? launcherVersion = null,
        string versionTypePrefix = "WorldBirdLauncher",
        CancellationToken cancellationToken = default)
    {
        var nativesDirectory = ResolveNativesDirectory();
        var warnings = new List<string>();
        var segments = new List<string>
        {
            $"-Xmn{minMemoryMb}M -Xmx{maxMemoryMb}M",
            await BuildJvmSegmentAsync(
                    account,
                    nativesDirectory,
                    launcherBrand,
                    launcherVersion ?? DefaultLauncherVersion,
                    warnings,
                    cancellationToken)
                .ConfigureAwait(false),
            BuildWindowSegment(windowWidth, windowHeight, fullScreen),
            BuildGameSegment(account, isVersionIsolation, versionTypePrefix),
        };

        return new GameLaunchArguments(segments, nativesDirectory, ClientJarPath, MainClass)
        {
            Warnings = warnings,
        };
    }

    // ---------- JVM 段 ----------

    private async Task<string> BuildJvmSegmentAsync(
        AccountProfile account,
        string nativesDirectory,
        string launcherBrand,
        string launcherVersion,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var args = new List<string>();

        // 账户专属 javaagent：统一通行证 → nide8auth，外置登录 → authlib-injector（与 StarLight 相同）
        var agentDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorldBirdLauncher", "agent");
        if (account.Kind == AccountKind.统一通行证)
        {
            var agent = await EnsureAgentAsync(
                    Path.Combine(agentDirectory, "nide8auth.jar"), Nide8AuthUrl, cancellationToken)
                .ConfigureAwait(false);
            if (agent is not null)
            {
                args.Add($"-javaagent:\"{agent}\"={account.UnifiedPassServerId}");
            }
            else
            {
                warnings.Add("没能准备好 nide8auth.jar（网络不通？），本次启动不会注入统一通行证，游戏内可能登录失败。");
            }
        }
        else if (account.Kind == AccountKind.外置)
        {
            var agent = await EnsureAuthlibInjectorAsync(agentDirectory, cancellationToken).ConfigureAwait(false);
            if (agent is not null)
            {
                args.Add($"-javaagent:\"{agent}\"={account.ServerUrl}");
            }
            else
            {
                warnings.Add("没能准备好 authlib-injector.jar（网络不通？），本次启动不会注入外置登录，游戏内可能登录失败。");
            }
        }

        if (_chain.Self.IsNewVersion)
        {
            args.Add($"-Dminecraft.client.jar=\"{ClientJarPath}\"");
        }

        if (IsWindows10OrGreater())
        {
            args.Add("-Dos.name=\"Windows 10\" -Dos.version=10.0");
        }

        args.Add(string.Join(" ", DefaultGcArguments.Concat(DefaultAdvancedArguments)
            .Concat(OptimizationGcArguments).Concat(OptimizationAdvancedArguments)));

        var placeholders = new Dictionary<string, string>
        {
            ["${natives_directory}"] = $"\"{nativesDirectory}\"",
            ["${launcher_name}"] = launcherBrand,
            ["${launcher_version}"] = launcherVersion,
            ["${classpath}"] = $"\"{BuildClasspath()}\"",
            // ★ 这两个值必须**带引号**：JVM 模板里的令牌（如 `-DignoreList=client-extra,${version_name}.jar`）
            //   替换后可能带空格（整合包实例名 `XPlus PerioTable based on …`）→ 不加引号会被命令行拆成多个参数，
            //   游戏侧就会把 `PerioTable` 当成主类直接 ClassNotFoundException（2026-10-07 真机踩到）。
            //   引号只影响命令行解析，解析后引号消失，`"名字".jar` 仍然拼成同一个文件名。
            ["${version_name}"] = $"\"{_chain.Self.Id}\"",
            ["${library_directory}"] = $"\"{Path.Combine(_root, "libraries")}\"",
            ["${classpath_separator}"] = ";",
        };

        args.Add(ReplacePlaceholders(BuildJvmTemplate(), placeholders));

        // 差异点①：StarLight 这里会插一段 `-Doolloo.jlw.tmpdir="…" -jar "…launch_wrapper.jar"`，我们不用它。
        args.Add(MainClass);

        return string.Join(" ", args);
    }

    /// <summary>
    /// JVM 参数模板：沿版本链收集 <c>arguments.jvm</c>（**跳过带 rules 的元素**、逐个去掉空格——
    /// 这两点是 StarLight 的行为，必须照做）；链上一个都没有才用固定的默认模板。
    /// 之所以"收集"而不是"看自己是不是新版本"：Quilt 的官方 profile **连 arguments 字段都没有**，
    /// 但它父版本（原版）有完整的 <c>-Djava.library.path</c> / <c>-cp ${classpath}</c>——必须拿出来用。
    /// </summary>
    private string BuildJvmTemplate()
    {
        var template = new List<string>();
        foreach (var entry in _chain.Entries.Skip(1).Reverse().Concat([_chain.Self]))
        {
            if (entry.ArgumentsJvm is not { ValueKind: JsonValueKind.Array } jvm)
            {
                continue;
            }

            foreach (var element in jvm.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("rules", out _))
                {
                    continue; // StarLight 只处理不带 rules 的条目（带 rules 的它一律丢掉）
                }

                template.Add(ElementText(element).Replace(" ", string.Empty));
            }
        }

        return template.Count == 0 ? string.Join(" ", DefaultJvmTemplate) : string.Join(" ", template);
    }

    private string BuildClasspath()
    {
        var librariesPath = Path.Combine(_root, "libraries");
        var normal = new List<string>();
        var optifine = new List<string>();
        var best = new Dictionary<string, (string Version, string Path)>(StringComparer.OrdinalIgnoreCase);

        // StarLight 的顺序：先父版本 JSON 的库，再自身的库（同一 group:artifact 只保留高版本）
        foreach (var entry in _chain.Entries.Skip(1).Reverse().Concat([_chain.Self]))
        {
            foreach (var library in entry.Libraries)
            {
                var name = ReadString(library, "name");
                if (string.IsNullOrEmpty(name) || !ShouldIncludeLibrary(library))
                {
                    continue;
                }

                var parts = name!.Split(':');
                var isPlatformSpecific = parts.Length >= 4;

                if (!isPlatformSpecific &&
                    library.TryGetProperty("downloads", out var downloads) &&
                    downloads.ValueKind == JsonValueKind.Object &&
                    downloads.TryGetProperty("classifiers", out var classifiers) &&
                    classifiers.ValueKind == JsonValueKind.Object &&
                    classifiers.EnumerateObject().Any())
                {
                    continue; // StarLight：非 4 段（无 natives 后缀）但带 classifiers 的条目整条跳过
                }

                if (BuildLibraryPath(name, librariesPath) is not { Length: > 0 } path)
                {
                    continue;
                }

                var isOptifine = name.StartsWith("optifine", StringComparison.OrdinalIgnoreCase);
                var target = isOptifine ? optifine : normal;

                if (isPlatformSpecific || parts.Length < 3)
                {
                    target.Add(path);
                    continue;
                }

                var key = parts[0] + ":" + parts[1];
                var version = parts[2];
                if (best.TryGetValue(key, out var cached))
                {
                    if (CompareVersions(version, cached.Version) <= 0)
                    {
                        continue;
                    }

                    normal.Remove(cached.Path);
                    optifine.Remove(cached.Path);
                }

                best[key] = (version, path);
                target.Add(path);
            }
        }

        // StarLight 把 optifine 的库统一排在普通库后面，最后再挂上客户端 jar
        var classpath = new List<string>(normal.Count + optifine.Count + 1);
        classpath.AddRange(normal);
        classpath.AddRange(optifine);
        classpath.Add(ClientJarPath);
        return string.Join(";", classpath);
    }

    /// <summary>按名字拼库路径（<c>group:artifact:version[:classifier]</c>）——与 StarLight 的 BuildFromName 一致。</summary>
    private static string? BuildLibraryPath(string name, string librariesPath)
    {
        var parts = name.Split(':');
        if (parts.Length is not (3 or 4))
        {
            return null;
        }

        // Maven 坐标支持 `group:artifact:version@扩展名`（NeoForge 的库大量使用，例如
        // `commons-io:commons-io:2.15.1@jar`）。真实文件名是 `commons-io-2.15.1.jar`，
        // 不能拼成 `commons-io-2.15.1@jar.jar` —— 那样文件不存在，模块解析会报
        // `FindException: Module org.apache.commons.io not found`（实测踩过）。
        var version = parts[2];
        var classifier = parts.Length == 4 ? parts[3] : null;
        var extension = "jar";
        // `@扩展名` 既可能跟在版本号后（`g:a:1.0@jar`），也可能跟在分类器后（`g:a:1.0:api@jar`）。
        version = StripExtension(version, ref extension);
        if (classifier is not null)
        {
            classifier = StripExtension(classifier, ref extension);
        }

        var groupPath = parts[0].Replace('.', Path.DirectorySeparatorChar);
        var fileName = classifier is null
            ? $"{parts[1]}-{version}.{extension}"
            : $"{parts[1]}-{version}-{classifier}.{extension}";
        return Path.Combine(librariesPath, groupPath, parts[1], version, fileName);
    }

    /// <summary>把 <c>1.0@jar</c> 拆成版本 <c>1.0</c> + 扩展名 <c>jar</c>。</summary>
    private static string StripExtension(string value, ref string extension)
    {
        var atIndex = value.IndexOf('@');
        if (atIndex < 0)
        {
            return value;
        }

        extension = value[(atIndex + 1)..];
        return value[..atIndex];
    }

    /// <summary>
    /// 版本比较：数字段按数值比、非数字段按序数比（StarLight 的同名库去重语义）。
    /// </summary>
    private static int CompareVersions(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        var leftParts = left.Split('.', '-');
        var rightParts = right.Split('.', '-');
        var length = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < leftParts.Length ? leftParts[i] : "0";
            var b = i < rightParts.Length ? rightParts[i] : "0";
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(a, out var leftNumber) && int.TryParse(b, out var rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }

            var comparison = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    // ---------- 窗口段 / 游戏段 ----------

    private static string BuildWindowSegment(int width, int height, bool fullScreen) =>
        fullScreen ? $"--width {width} --height {height} --fullscreen" : $"--width {width} --height {height}";

    private string BuildGameSegment(AccountProfile account, bool isVersionIsolation, string versionTypePrefix)
    {
        var self = _chain.Self;
        string gameArguments;
        if (!string.IsNullOrWhiteSpace(self.MinecraftArguments))
        {
            // 老版本（≤1.12.2）的模组版本 JSON 自带**完整**的 minecraftArguments——实测 1.12.2-Forge 与
            // 1.7.10-Forge 的 JSON 里 `--username/--gameDir/--assetsDir/…` 一个不少，只多出一个 `--tweakClass`。
            // 这时再按 StarLight 的老做法拼上父版本那份，就会变成两份 `--gameDir`，游戏直接报
            // `MultipleArgumentsForOptionException` 起不来（实测）。所以老版本只用子版本自己那一份。
            gameArguments = self.MinecraftArguments!;
        }
        else
        {
            // 新版本（1.13+）：自己那份（Fabric 的 game 是空数组、Forge 1.13+ 只有 fml 参数）+ 父版本那份。
            // Quilt 那种连 arguments 都没有的 profile 自己拿不到东西，就整个回落到父版本。
            gameArguments = self.IsNewVersion ? JoinGameElements(self) : string.Empty;
            foreach (var entry in _chain.Entries.Skip(1))
            {
                var inherited = entry.IsNewVersion ? JoinGameElements(entry) : entry.MinecraftArguments ?? string.Empty;
                if (inherited.Length > 0)
                {
                    gameArguments = gameArguments.Length == 0 ? inherited : gameArguments + " " + inherited;
                }
            }
        }

        // StarLight 会把 --tweakClass 挪到游戏参数最后（OptiFine / Forge 的 tweaker 有顺序要求）
        foreach (var tweakClass in new[] { "--tweakClass optifine.OptiFineForgeTweaker ", "--tweakClass optifine.OptiFineTweaker " })
        {
            if (!gameArguments.Contains(tweakClass, StringComparison.Ordinal))
            {
                continue;
            }

            gameArguments = gameArguments.Replace(tweakClass, string.Empty).Trim();
            gameArguments = $"{gameArguments} {tweakClass}".Trim();
            break;
        }

        var gameDirectory = isVersionIsolation
            ? Path.Combine(_root, "versions", _versionId)
            : _root;

        var placeholders = new Dictionary<string, string>
        {
            ["${auth_player_name}"] = account.Name,
            ["${version_name}"] = $"\"{_versionId}\"",
            ["${assets_root}"] = $"\"{Path.Combine(_root, "assets")}\"",
            ["${assets_index_name}"] = ResolveAssetsIndexName(),
            ["${auth_uuid}"] = ResolveUuid(account).Replace("-", string.Empty),
            ["${auth_access_token}"] = ResolveAccessToken(account),
            ["${user_type}"] = account.Kind == AccountKind.微软 ? "msa" : "Mojang",
            ["${version_type}"] = $"\"{versionTypePrefix}/{ToTitleCase(ResolveVersionType())}\"",
            ["${user_properties}"] = "{}",
            ["${game_directory}"] = $"\"{gameDirectory}\"",
        };

        return CleanUnresolvedPlaceholders(ReplacePlaceholders(gameArguments, placeholders));
    }

    private static string JoinGameElements(MinecraftVersionEntry entry)
    {
        if (entry.ArgumentsGame is not { ValueKind: JsonValueKind.Array } game)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var element in game.EnumerateArray())
        {
            // 带 rules 的游戏参数（如 demo 模式）StarLight 一律跳过
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("rules", out _))
            {
                continue;
            }

            parts.Add(ElementText(element));
        }

        return string.Join(" ", parts);
    }

    /// <summary>差异点③：把没能替换掉的占位符连同它的选项一起丢掉（StarLight 会把 `${clientid}` 原样传给游戏）。</summary>
    private static string CleanUnresolvedPlaceholders(string arguments)
    {
        if (!arguments.Contains("${", StringComparison.Ordinal))
        {
            return arguments;
        }

        var cleaned = OptionWithPlaceholder.Replace(arguments, string.Empty);
        cleaned = LeftoverPlaceholder.Replace(cleaned, string.Empty);
        return RepeatedSpace.Replace(cleaned, " ").Trim();
    }

    /// <summary>资源索引名：自身没写就沿继承链往上找（Fabric/Forge 的 JSON 里通常不带 assets）。</summary>
    private string ResolveAssetsIndexName()
    {
        foreach (var entry in _chain.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Assets))
            {
                return entry.Assets!;
            }
        }

        return string.Empty;
    }

    /// <summary>版本类型（release / snapshot / old_beta…），与 StarLight 一样取自版本 JSON。</summary>
    private string ResolveVersionType() =>
        string.IsNullOrWhiteSpace(_chain.Self.Type) ? "release" : _chain.Self.Type!;

    // ---------- natives ----------

    /// <summary>
    /// natives 目标目录。与 StarLight 相同的部分：优先 <c>versions\&lt;id&gt;\natives</c>，
    /// 没有就在版本目录下找第一个名字含 <c>natives</c> 的目录（它会把文件解到 <c>&lt;id&gt;-natives</c>）。
    /// 差异点④：两者都没有时返回 <c>&lt;id&gt;-natives</c>（我们真的要解压到那里），而不是 StarLight 那个不存在的 <c>natives</c>。
    /// </summary>
    public string ResolveNativesDirectory()
    {
        var versionDirectory = Path.Combine(_root, "versions", _versionId);
        var preferred = Path.Combine(versionDirectory, "natives");
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        try
        {
            var candidates = Directory.Exists(versionDirectory)
                ? Directory.GetDirectories(versionDirectory, "*natives*", SearchOption.AllDirectories)
                : [];
            if (candidates.Length > 0)
            {
                return candidates[0];
            }
        }
        catch (Exception)
        {
            // 读不了就按下面的兜底路径走
        }

        return Path.Combine(versionDirectory, _versionId + "-natives");
    }

    /// <summary>
    /// 解压 natives，返回解压出的文件数。
    ///
    /// 与 StarLight 的 <c>FileUtil.DecompressionNatives</c> 相比：沿**整条版本链**取 natives 包
    /// （Fabric/Forge 自己的 JSON 里没有 libraries，StarLight 那样只读顶层会一个都不解）、
    /// 只挑当前架构的包、并且除 META-INF 外全都解出来（它只解 .dll 且平铺）。
    /// </summary>
    public int ExtractNatives(string nativesDirectory, Action<string>? onLog = null)
    {
        var extracted = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _chain.Entries)
        {
            foreach (var library in entry.Libraries)
            {
                if (!ShouldIncludeLibrary(library) || ResolveNativesJar(library) is not { } jarPath)
                {
                    continue;
                }

                if (!File.Exists(jarPath) || !seen.Add(jarPath))
                {
                    continue;
                }

                try
                {
                    extracted += ExtractJar(jarPath, nativesDirectory);
                }
                catch (Exception ex)
                {
                    onLog?.Invoke($"提示：解压 {Path.GetFileName(jarPath)} 失败（{ex.Message}），继续解其它包。");
                }
            }
        }

        return extracted;
    }

    /// <summary>这个库在本机需要解压的 natives 包（<c>downloads.classifiers</c> 或 <c>natives-*</c> 命名）。</summary>
    private string? ResolveNativesJar(JsonElement library)
    {
        var name = ReadString(library, "name");
        var librariesPath = Path.Combine(_root, "libraries");

        if (name is null)
        {
            return null;
        }

        var parts = name.Split(':');
        if (parts.Length > 3 && parts[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
        {
            // 新格式（1.19+）：库名自带 natives 分类器，另有 arm64/x86 条目要按架构挑
            return MatchesCurrentArchitecture(parts[3]) ? BuildLibraryPath(name, librariesPath) : null;
        }

        if (!library.TryGetProperty("downloads", out var downloads) ||
            downloads.ValueKind != JsonValueKind.Object ||
            !downloads.TryGetProperty("classifiers", out var classifiers) ||
            classifiers.ValueKind != JsonValueKind.Object ||
            !library.TryGetProperty("natives", out var natives) ||
            natives.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var osKey = NetCompat.IsWindows() ? "windows" : NetCompat.IsMacOS() ? "osx" : "linux";
        if (!natives.TryGetProperty(osKey, out var classifierName) || classifierName.ValueKind != JsonValueKind.String)
        {
            return null; // 有 natives 映射但本机没条目：这个库本机不需要 natives 包
        }

        var key = classifierName.GetString()!
            .Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
        if (!classifiers.TryGetProperty(key, out var classifier) ||
            !classifier.TryGetProperty("path", out var path) ||
            path.GetString() is not { Length: > 0 } relative)
        {
            return null;
        }

        return Path.Combine(librariesPath, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>分类器是否匹配当前架构（x64 机器上别把 <c>-arm64</c>/<c>-x86</c> 的 dll 也盖进来）。</summary>
    private static bool MatchesCurrentArchitecture(string classifier)
    {
        var isArm64 = classifier.Contains("arm64", StringComparison.OrdinalIgnoreCase);
        var isX86 = !isArm64 && classifier.EndsWith("-x86", StringComparison.OrdinalIgnoreCase);
        var runningArm64 = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64;
        var runningX86 = !Environment.Is64BitOperatingSystem;

        if (isArm64)
        {
            return runningArm64;
        }

        return !isX86 || runningX86;
    }

    private static int ExtractJar(string jarPath, string nativesDirectory)
    {
        Directory.CreateDirectory(nativesDirectory);
        var extracted = 0;
        using var archive = ZipFile.OpenRead(jarPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') ||
                entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 平铺到 natives 目录：java.library.path 只搜当前层，不递归子目录
            var target = Path.Combine(nativesDirectory, entry.Name);
            entry.ExtractToFile(target, overwrite: true);
            extracted++;
        }

        return extracted;
    }

    // ---------- 通用小工具 ----------

    /// <summary>
    /// StarLight 的 <c>FileUtil.ShouldIncludeLibrary</c>：**只看 <c>os.name</c>**（不看 arch/version/features），
    /// 没有 rules 就包含；最后一条匹配的规则生效。这里照搬它的判定，保证 classpath 与它一字不差。
    /// </summary>
    private static bool ShouldIncludeLibrary(JsonElement library)
    {
        if (!library.TryGetProperty("rules", out var rules) ||
            rules.ValueKind != JsonValueKind.Array ||
            rules.GetArrayLength() == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var matches = true;
            if (rule.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(os, "name");
                matches = string.Equals(name?.ToLowerInvariant(), "windows", StringComparison.Ordinal);
            }

            if (!matches)
            {
                continue;
            }

            allowed = rule.TryGetProperty("action", out var action) &&
                      string.Equals(action.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    private static string ElementText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => element.GetRawText(),
    };

    private static string ReplacePlaceholders(string template, Dictionary<string, string> placeholders)
    {
        var result = template;
        foreach (var (key, value) in placeholders)
        {
            result = NetCompat.Replace(result, key, value, StringComparison.Ordinal);
        }

        return result;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>与 <c>SystemUtil.IsOperatingSystemGreaterThanWin10</c> 一致：读注册表判断 Win10+。</summary>
    private static bool IsWindows10OrGreater()
    {
        if (!NetCompat.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("CurrentMajorVersionNumber") is int major && major >= 10;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ToTitleCase(string value) =>
        System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value);

    private static string ResolveAccessToken(AccountProfile account) =>
        string.IsNullOrWhiteSpace(account.AccessToken) ? "0" : account.AccessToken;

    private static string ResolveUuid(AccountProfile account)
    {
        if (!string.IsNullOrWhiteSpace(account.Uuid))
        {
            return account.Uuid;
        }

        var hash = NetCompat.Md5(
            System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + account.Name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return NetCompat.ToHexString(hash).ToLowerInvariant();
    }

    private string ResolveClientJarPath()
    {
        // 官方规则：优先用**版本目录自己的** <版本名>.jar；没有才继承父版本的（Fabric/Quilt/老 Forge 都没有自己的 jar）。
        // 模块化加载器（Forge 1.17+ / NeoForge）必须在版本目录里放一份以版本名命名的客户端 jar：
        // 它们的 `-DignoreList=client-extra,${version_name}.jar` 就是按**文件名**把这份 jar 排除在模块层之外的
        // （否则原版 jar 会变成自动模块 `_1._21._1`，和 patched client 的 `minecraft` 模块包名冲突，实测直接崩）。
        //
        // ★ 必须**沿整条链**找：整合包实例目录没有 jar，链中间的加载器目录才有，
        //   直接取链尾（原版）会拿错 jar（实测：主类/客户端 jar 都错，启动崩）。
        foreach (var entry in _chain.Entries)
        {
            var candidate = Path.Combine(_root, "versions", entry.Id, entry.Id + ".jar");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var root = _chain.Entries.Count > 1 ? _chain.Entries[^1] : _chain.Self;
        return Path.Combine(_root, "versions", root.Id, root.Id + ".jar");
    }

    private static async Task<string?> EnsureAgentAsync(
        string targetPath,
        string url,
        CancellationToken cancellationToken)
    {
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            return targetPath;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AgentDownloadTimeout); // 网络不通时别把"点启动 → 进游戏"卡住几分钟
            var bytes = await Http.GetByteArrayAsync(url, timeout.Token).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            await NetCompat.WriteAllBytesAsync(targetPath, bytes, timeout.Token).ConfigureAwait(false);
            return targetPath;
        }
        catch (Exception)
        {
            return null; // 下载不到就不加 javaagent：让游戏用原版方式启动，总比起不来好
        }
    }

    private static async Task<string?> EnsureAuthlibInjectorAsync(
        string agentDirectory,
        CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(agentDirectory, "authlib-injector.jar");
        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
        {
            return targetPath;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AgentDownloadTimeout);
            var json = await Http.GetStringAsync(AuthlibLatestUrl, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("download_url", out var url) ||
                url.GetString() is not { Length: > 0 } downloadUrl)
            {
                return null;
            }

            return await EnsureAgentAsync(targetPath, downloadUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => _chain.Dispose();
}
