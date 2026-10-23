using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McServerList.Core.Launcher;

/// <summary>
/// Forge 安装（自研）。
///
/// 背景：StarLight.Core 2.0.5 的 <c>ForgeInstaller</c> 其实是个空壳——只把 installer 下到临时目录、
/// 从里面取出 <c>install_profile.json</c> 读进变量就扔掉，既不写版本 JSON、也不下库、更不跑安装步骤，
/// 最后直接返回"安装完成"（本机那个空的 <c>1.13.2-Forge_25.0.53</c> 目录就是这么来的）。
///
/// 这里按 **Forge 官方安装器**的算法重写（源码见仓库外 `_probe\_refs\ForgeInstaller`，
/// `actions/ClientInstall.java` + `actions/PostProcessors.java` + `json/{Install,Artifact,Util}.java`）：
/// ① 把安装包里的 `maven/**` 全部解到 `libraries/`；
/// ② 新式安装包（spec 0/1，1.13+ 与新一点的 1.12.2）：`json` 指向的 version.json 写进版本目录，
///    `libraries` 下齐，然后**按顺序用 Java 跑 `processors`**（`{KEY}` / `[maven 坐标]` 参数用 `data` 表解析），
///    跑完校验 `outputs` 的 sha1；
/// ③ 老式安装包（1.7.10 等，只有 `install` + `versionInfo`）：版本 JSON 用内嵌的 `versionInfo`，
///    把 `install.filePath` 解到 `install.path` 对应的 maven 路径，没有 processors。
///
/// 失败时**不删任何东西**（用户要求）：半成品留在版本目录里，下次重装可以直接复用已下好的文件；
/// 提示里会写清楚怎么彻底重来。
/// </summary>
public sealed class ForgeInstallerService
{
    private const string OfficialMaven = "https://maven.minecraftforge.net/";
    private const string BmclMaven = "https://bmclapi2.bangbang93.com/maven/";
    private const string OfficialNeoForgeMaven = "https://maven.neoforged.net/releases/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>要装哪一家：Forge 与 NeoForge 的安装包结构完全同源（spec 0/1 + processors），只是仓库地址和目录名不同。</summary>
    public enum LoaderKind
    {
        Forge,
        NeoForge,
    }

    /// <summary>安装一个 Forge 版本。返回的 <see cref="InstallOutcome.VersionId"/> 是版本目录名。</summary>
    public async Task<InstallOutcome> InstallAsync(
        string gameVersion,
        string forgeVersion,
        string gameRoot,
        bool useMirror,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        LoaderKind kind = LoaderKind.Forge)
    {
        var versionId = VersionId(kind, gameVersion, forgeVersion);
        var versionDirectory = Path.Combine(gameRoot, "versions", versionId);

        try
        {
            // ① 原版（父版本）必须先有：processors 要用 {MINECRAFT_JAR}，库/资源也靠它。
            //
            // ★ 踩过的坑（2026-10-07 阶段 4 回归）：这里以前**只判断父版本的 jar 在不在**，而
            //   InstallWithFallbackAsync 一进来就会用 EnsureClientJarAsync 把父版本的 jar 先放好，
            //   于是"补装父版本"被整个跳过 —— 父版本的**版本 JSON / 库 / 资源**从来没装。后果：
            //   完整性校验读不到 inheritsFrom 的父版本 JSON → 误报"<版本>.jar 缺失"；启动时版本链也少一半。
            //   判断条件必须连版本 JSON 一起看（JSON 才代表"这个版本真的装过"）。
            var vanillaDirectory = Path.Combine(gameRoot, "versions", gameVersion);
            var vanillaJar = Path.Combine(vanillaDirectory, gameVersion + ".jar");
            var vanillaJson = Path.Combine(vanillaDirectory, gameVersion + ".json");
            if (!File.Exists(vanillaJar) || !File.Exists(vanillaJson))
            {
                progress?.Report(new InstallProgress($"Forge 需要先安装原版 {gameVersion}", 3));
                var vanilla = await new GameInstallationService()
                    .InstallWithFallbackAsync(gameVersion, "原版", null, gameRoot, useMirror, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!vanilla.Success)
                {
                    return InstallOutcome.Fail($"Forge 需要先安装原版 {gameVersion}，但原版安装失败：{vanilla.Message}");
                }
            }

            // ② 下载安装包
            progress?.Report(new InstallProgress("正在下载 Forge 安装包", 10));
            var installerPath = await DownloadInstallerAsync(gameVersion, forgeVersion, useMirror, kind, cancellationToken)
                .ConfigureAwait(false);
            if (installerPath is null)
            {
                return InstallOutcome.Fail(
                    $"没找到 {LoaderName(kind)} {gameVersion}-{forgeVersion} 的安装包（官方 maven 与 BMCLAPI 都没有）。");
            }

            using var archive = ZipFile.OpenRead(installerPath);
            var profileEntry = FindEntry(archive, "install_profile.json")
                ?? throw new InvalidOperationException("安装包里没有 install_profile.json。");
            using var profileDocument = JsonDocument.Parse(ReadEntryText(profileEntry));
            var profile = profileDocument.RootElement;

            Directory.CreateDirectory(versionDirectory);

            // ③ 安装包自带的 maven/ 全部解到 libraries/
            var extracted = ExtractMavenDirectory(archive, gameRoot);
            if (extracted > 0)
            {
                progress?.Report(new InstallProgress($"已从安装包解出 {extracted} 个自带库文件", 25));
            }

            var isLegacy = profile.TryGetProperty("versionInfo", out _);
            int writtenLibraries;
            var jsonPath = Path.Combine(versionDirectory, versionId + ".json");
            if (isLegacy)
            {
                writtenLibraries = WriteLegacyFiles(archive, profile, versionId, versionDirectory, jsonPath, gameRoot);
            }
            else
            {
                writtenLibraries = WriteSpecFiles(archive, profile, versionId, jsonPath);
            }

            if (!File.Exists(jsonPath))
            {
                return InstallOutcome.Fail("没能从安装包里取出这个 Forge 版本的版本 JSON。");
            }

            // 模块化加载器（Forge 1.17+ / NeoForge）需要在版本目录里放一份以**版本名**命名的客户端 jar：
            // 它们的 jvm 参数是 `-DignoreList=client-extra,${version_name}.jar`，按文件名把这份 jar 排除在模块层外。
            // 缺了它，我们只能拿父版本的原版 jar 当客户端 jar → 它会变成自动模块和 patched client 的 `minecraft`
            // 模块包名冲突（实测 `ResolutionException: Module minecraft contains package ...`）。
            EnsureModuleAwareClientJar(gameVersion, versionId, gameRoot, jsonPath, progress);

            // ④ 下载安装包声明的库 + 版本 JSON 里的库
            progress?.Report(new InstallProgress("正在下载 Forge 依赖库", 40));
            var downloader = new GameFileDownloader();
            var versionFiles = await downloader
                .DownloadAsync(versionId, gameRoot, useMirror, 8, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!versionFiles.Success)
            {
                return InstallOutcome.Fail(KeepMessage(versionDirectory, $"版本依赖库没下全：{versionFiles.Message}"));
            }

            if (writtenLibraries > 0)
            {
                var profileFiles = await downloader
                    .DownloadPlannedAsync(
                        BuildProfileLibraryPlan(profile, gameRoot), gameRoot, useMirror, 8, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!profileFiles.Success)
                {
                    return InstallOutcome.Fail(KeepMessage(versionDirectory, $"安装器依赖库没下全：{profileFiles.Message}"));
                }
            }

            // ⑤ 新式安装包要跑 processors（老的没有）
            if (!isLegacy && profile.TryGetProperty("processors", out var processors) &&
                processors.ValueKind == JsonValueKind.Array && processors.GetArrayLength() > 0)
            {
                var run = await RunProcessorsAsync(
                        profile, processors, gameRoot, versionId, installerPath, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (!run.Success)
                {
                    return InstallOutcome.Fail(KeepMessage(versionDirectory, run.Message));
                }
            }

            progress?.Report(new InstallProgress("Forge 安装完成", 100));
            return InstallOutcome.Ok(versionId, $"{LoaderName(kind)} {gameVersion}-{forgeVersion} 安装完成。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return InstallOutcome.Fail(KeepMessage(versionDirectory, $"Forge 安装失败：{ex.Message}"));
        }
    }

    /// <summary>Forge 版本的目录名约定：<c>&lt;游戏版本&gt;-Forge_&lt;forge版本&gt;</c>（与已有目录、StarLight 命名一致）。</summary>
    public static string ForgeVersionId(string gameVersion, string forgeVersion) =>
        $"{gameVersion}-Forge_{forgeVersion}";

    /// <summary>按加载器种类给版本目录名：<c>&lt;mc&gt;-Forge_&lt;ver&gt;</c> / <c>&lt;mc&gt;-neoforge_&lt;ver&gt;</c>。</summary>
    public static string VersionId(LoaderKind kind, string gameVersion, string loaderVersion) =>
        kind == LoaderKind.NeoForge
            ? $"{gameVersion}-neoforge_{loaderVersion}"
            : ForgeVersionId(gameVersion, loaderVersion);

    private static string LoaderName(LoaderKind kind) => kind == LoaderKind.NeoForge ? "NeoForge" : "Forge";

    /// <summary>用户要求：失败不删东西，只把"半成品在哪、怎么重来"说清楚。</summary>
    private static string KeepMessage(string versionDirectory, string message) =>
        $"{message}\n半成品已保留在 {versionDirectory}（下次重装可以直接复用已下好的文件）。" +
        "想彻底重来就删掉这个版本目录，或者用「安装版本…」里的重装。";

    /// <summary>
    /// 模块化加载器（jvm 参数里出现 <c>-DignoreList</c> 的就是，Forge 1.17+ / NeoForge）需要在版本目录里
    /// 放一份以版本名命名的客户端 jar：把父版本（原版）的 jar 原样复制过去即可——它的作用是"给启动器一个
    /// 名字对得上的客户端 jar 占位"，让 `-DignoreList=...,${version_name}.jar` 能把它排除在模块层之外。
    /// </summary>
    private static void EnsureModuleAwareClientJar(
        string gameVersion,
        string versionId,
        string gameRoot,
        string jsonPath,
        IProgress<InstallProgress>? progress)
    {
        try
        {
            var json = File.ReadAllText(jsonPath);
            if (!json.Contains("-DignoreList", StringComparison.Ordinal))
            {
                return; // 非模块化的 Forge（1.13-1.16 那一代）不需要
            }

            var ownJar = Path.Combine(gameRoot, "versions", versionId, versionId + ".jar");
            if (File.Exists(ownJar))
            {
                return;
            }

            var vanillaJar = Path.Combine(gameRoot, "versions", gameVersion, gameVersion + ".jar");
            if (!File.Exists(vanillaJar))
            {
                return;
            }

            progress?.Report(new InstallProgress("正在准备模块化加载器需要的客户端 jar", 35));
            File.Copy(vanillaJar, ownJar, overwrite: true);
        }
        catch (Exception)
        {
            // 复制失败不阻断安装：启动时缺 jar 会给出更明确的错误
        }
    }

    // ---------- 安装包结构 ----------

    /// <summary>spec 0/1：版本 JSON 在 <c>json</c> 指向的条目里，把 id 改成我们的目录名后落盘。</summary>
    private static int WriteSpecFiles(ZipArchive archive, JsonElement profile, string versionId, string jsonPath)
    {
        var jsonName = profile.TryGetProperty("json", out var jsonField) ? jsonField.GetString() : null;
        var entry = FindEntry(archive, (jsonName ?? "/version.json").TrimStart('/'))
            ?? throw new InvalidOperationException($"安装包里没有版本 JSON（{jsonName}）。");

        var node = JsonNode.Parse(ReadEntryText(entry)) as JsonObject
            ?? throw new InvalidOperationException("安装包里的版本 JSON 不是对象。");
        node["id"] = versionId;
        File.WriteAllText(jsonPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        // 返回"安装包自己声明的库"数量，0 表示不用再下一轮
        return profile.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array
            ? libraries.GetArrayLength()
            : 0;
    }

    /// <summary>老式安装包：版本 JSON 用内嵌的 versionInfo；<c>install.filePath</c> 解到 <c>install.path</c> 的 maven 路径。</summary>
    private static int WriteLegacyFiles(
        ZipArchive archive,
        JsonElement profile,
        string versionId,
        string versionDirectory,
        string jsonPath,
        string gameRoot)
    {
        var versionInfo = profile.GetProperty("versionInfo");
        var node = JsonNode.Parse(versionInfo.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("安装包里的 versionInfo 不是对象。");
        node["id"] = versionId;
        File.WriteAllText(jsonPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var written = 0;
        if (profile.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Object)
        {
            var filePath = GetString(install, "filePath");
            var mavenPath = GetString(install, "path");
            if (!string.IsNullOrWhiteSpace(filePath) && !string.IsNullOrWhiteSpace(mavenPath) &&
                MavenArtifact.TryParse(mavenPath!, out var artifact))
            {
                var entry = FindEntry(archive, filePath!.TrimStart('/'));
                if (entry is not null)
                {
                    var target = artifact.LocalPath(gameRoot);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    written++;
                }
            }
        }

        _ = versionDirectory;

        // 老式安装包的顶层 libraries（若有）也要下
        return written + (profile.TryGetProperty("libraries", out var libraries) &&
                          libraries.ValueKind == JsonValueKind.Array
            ? libraries.GetArrayLength()
            : 0);
    }

    /// <summary>把安装包里的 <c>maven/</c> 全部解到 <c>libraries/</c>（官方安装器就是从 /maven/ 里取自带构件）。</summary>
    private static int ExtractMavenDirectory(ZipArchive archive, string gameRoot)
    {
        var count = 0;
        var librariesRoot = Path.Combine(gameRoot, "libraries");
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("maven/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var relative = entry.FullName["maven/".Length..];
            var target = Path.Combine(librariesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            count++;
        }

        return count;
    }

    // ---------- 库 ----------

    /// <summary>安装包 libraries 的下载清单：优先用 downloads.artifact（含 url/sha1），否则按 maven 坐标拼。</summary>
    private static List<GameFileDownloader.PlannedFile> BuildProfileLibraryPlan(JsonElement profile, string gameRoot)
    {
        var planned = new List<GameFileDownloader.PlannedFile>();
        if (!profile.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
        {
            return planned;
        }

        foreach (var library in libraries.EnumerateArray())
        {
            var name = GetString(library, "name");
            if (string.IsNullOrWhiteSpace(name) || !MavenArtifact.TryParse(name!, out var artifact))
            {
                continue;
            }

            string? officialUrl = null;
            long size = 0;
            if (library.TryGetProperty("downloads", out var downloads) &&
                downloads.ValueKind == JsonValueKind.Object &&
                downloads.TryGetProperty("artifact", out var artifactNode) &&
                artifactNode.ValueKind == JsonValueKind.Object)
            {
                officialUrl = GetString(artifactNode, "url");
                if (artifactNode.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var value))
                {
                    size = value;
                }
            }

            // downloads 里没有 url 的（例如 forge 的 universal）就按 maven 坐标拼
            if (string.IsNullOrWhiteSpace(officialUrl))
            {
                var baseUrl = GetString(library, "url");
                var isNeoForgeMaven = name!.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase);
                var isForgeMaven = name!.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase) ||
                                   name.StartsWith("de.oceanlabs.mcp:", StringComparison.OrdinalIgnoreCase);
                var fallbackBase = isNeoForgeMaven
                    ? OfficialNeoForgeMaven
                    : isForgeMaven ? OfficialMaven : "https://libraries.minecraft.net/";
                officialUrl = (string.IsNullOrWhiteSpace(baseUrl) ? fallbackBase : baseUrl!)
                    .TrimEnd('/') + "/" + artifact.RelativePath;
            }

            planned.Add(new GameFileDownloader.PlannedFile(
                artifact.LocalPath(gameRoot),
                officialUrl!,
                BmclMaven + artifact.RelativePath,
                size));
        }

        return planned;
    }

    // ---------- processors ----------

    private async Task<(bool Success, string Message)> RunProcessorsAsync(
        JsonElement profile,
        JsonElement processors,
        string gameRoot,
        string versionId,
        string installerPath,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        // data 表：`[maven 坐标]`、`'字面量'`、其它＝安装包内的文件（解到临时目录）
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "mcsl-forge-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (profile.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.Object)
            {
                using var installerArchive = ZipFile.OpenRead(installerPath);
                foreach (var property in dataNode.EnumerateObject())
                {
                    var raw = property.Value.ValueKind == JsonValueKind.Object
                        ? GetString(property.Value, "client")   // spec 0：{client, server}
                        : property.Value.ValueKind == JsonValueKind.String
                            ? property.Value.GetString()        // spec 1：直接是字符串
                            : null;
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    // 老框架的可空注解面不同，这里取一次非空引用，后面就不用满地写 !
                    var value = raw!;
                    if (value.StartsWith('[') && value.EndsWith(']'))
                    {
                        if (!MavenArtifact.TryParse(value[1..^1], out var artifact))
                        {
                            return (false, $"data 里的 maven 坐标无法解析：{value}");
                        }

                        data[property.Name] = artifact.LocalPath(gameRoot);
                    }
                    else if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
                    {
                        data[property.Name] = value[1..^1];
                    }
                    else
                    {
                        var target = Path.Combine(tempDirectory, value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        var entry = FindEntry(installerArchive, value.TrimStart('/'));
                        if (entry is null)
                        {
                            return (false, $"安装包里缺少 data 需要的文件：{value}");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                        data[property.Name] = target;
                    }
                }
            }

            data["SIDE"] = "client";
            data["MINECRAFT_JAR"] = Path.Combine(gameRoot, "versions", GetMinecraftVersion(profile, versionId),
                GetMinecraftVersion(profile, versionId) + ".jar");
            data["MINECRAFT_VERSION"] = GetMinecraftVersion(profile, versionId);
            data["ROOT"] = gameRoot;
            data["INSTALLER"] = installerPath;
            data["LIBRARY_DIR"] = Path.Combine(gameRoot, "libraries");

            var javaPath = PickJava(gameRoot, versionId);
            if (javaPath is null)
            {
                return (false,
                    $"这个 Forge 版本需要 Java {JavaRuntimeService.RequiredFeatureVersion(gameRoot, versionId)}，" +
                    "但本机没有可用的 Java。请先在设置页「下载适配 Java」装好再重装 Forge。");
            }

            var index = 0;
            foreach (var processor in processors.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                // sides 里有 client 才跑（没写就是两边都跑）
                if (processor.TryGetProperty("sides", out var sides) && sides.ValueKind == JsonValueKind.Array &&
                    !sides.EnumerateArray().Any(s => string.Equals(s.GetString(), "client", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var jarDescriptor = GetString(processor, "jar");
                if (string.IsNullOrWhiteSpace(jarDescriptor) || !MavenArtifact.TryParse(jarDescriptor!, out var processorJar))
                {
                    return (false, "processor 的 jar 坐标无法解析。");
                }

                var jarPath = processorJar.LocalPath(gameRoot);
                if (!File.Exists(jarPath))
                {
                    return (false, $"processor 缺少 jar：{jarDescriptor}");
                }

                var mainClass = ReadMainClass(jarPath);
                if (string.IsNullOrWhiteSpace(mainClass))
                {
                    return (false, $"processor 的 jar 没有 Main-Class：{Path.GetFileName(jarPath)}");
                }

                var classpath = new List<string> { jarPath };
                if (processor.TryGetProperty("classpath", out var deps) && deps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dep in deps.EnumerateArray())
                    {
                        var descriptor = dep.GetString();
                        if (string.IsNullOrWhiteSpace(descriptor) || !MavenArtifact.TryParse(descriptor!, out var dependency))
                        {
                            return (false, $"processor 依赖坐标无法解析：{descriptor}");
                        }

                        var path = dependency.LocalPath(gameRoot);
                        if (!File.Exists(path))
                        {
                            return (false, $"processor 缺少依赖：{descriptor}");
                        }

                        classpath.Add(path);
                    }
                }

                var args = new List<string>();
                if (processor.TryGetProperty("args", out var argNodes) && argNodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var argNode in argNodes.EnumerateArray())
                    {
                        var arg = argNode.GetString();
                        if (arg is null)
                        {
                            continue;
                        }

                        if (arg.StartsWith('[') && arg.EndsWith(']'))
                        {
                            if (!MavenArtifact.TryParse(arg[1..^1], out var artifact))
                            {
                                return (false, $"processor 参数里的 maven 坐标无法解析：{arg}");
                            }

                            args.Add(artifact.LocalPath(gameRoot));
                        }
                        else
                        {
                            args.Add(ReplaceTokens(data, arg));
                        }
                    }
                }

                progress?.Report(new InstallProgress($"正在执行 Forge 安装步骤 {index}/{processors.GetArrayLength()}", 60));
                var (exitCode, output) = await RunJavaAsync(javaPath, classpath, mainClass!, args, gameRoot, cancellationToken)
                    .ConfigureAwait(false);
                if (exitCode != 0)
                {
                    return (false, $"Forge 安装步骤 {index}（{mainClass}）执行失败，退出码 {exitCode}：\n{output}");
                }

                // 校验 outputs 的 sha1（官方安装器就是这么判断这一步有没有真做成）
                if (processor.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var outputEntry in outputs.EnumerateObject())
                    {
                        var key = outputEntry.Name;
                        var file = key.StartsWith('[') && key.EndsWith(']') && MavenArtifact.TryParse(key[1..^1], out var artifact)
                            ? artifact.LocalPath(gameRoot)
                            : data.TryGetValue(key, out var mapped) ? mapped : ReplaceTokens(data, key);
                        if (!File.Exists(file))
                        {
                            return (false, $"Forge 安装步骤 {index} 没有产出文件：{file}");
                        }

                        var expected = outputEntry.Value.ValueKind == JsonValueKind.String
                            ? ReplaceTokens(data, outputEntry.Value.GetString() ?? string.Empty).Trim('\'')
                            : string.Empty;
                        if (expected.Length > 0 && !string.Equals(Sha1(file), expected, StringComparison.OrdinalIgnoreCase))
                        {
                            return (false, $"Forge 安装步骤 {index} 的产物 sha1 不符：{file}");
                        }
                    }
                }
            }

            return (true, "Forge 安装步骤全部完成。");
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>
    /// 官方安装器是**在进程内反射调用** processor 的 main；我们等价地用子进程跑
    /// <c>java -cp &lt;jar;依赖…&gt; &lt;Main-Class&gt; &lt;args…&gt;</c>。
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunJavaAsync(
        string javaPath,
        IReadOnlyList<string> classpath,
        string mainClass,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // net48 没有 ProcessStartInfo.ArgumentList，统一走兼容层拼命令行（含引号转义）
        var commandLine = new List<string> { "-cp", string.Join(";", classpath), mainClass };
        foreach (var arg in args)
        {
            commandLine.Add(arg);
        }

        startInfo.Arguments = NetCompat.JoinArguments(commandLine);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return (-1, "无法启动 Java 进程。");
        }

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await NetCompat.WaitForExitAsync(process, cancellationToken).ConfigureAwait(false);
        var text = output.ToString().Trim();
        return (process.ExitCode, text.Length > 4000 ? text[^4000..] : text);
    }

    // ---------- 工具 ----------

    private async Task<string?> DownloadInstallerAsync(
        string gameVersion,
        string forgeVersion,
        bool useMirror,
        LoaderKind kind,
        CancellationToken cancellationToken)
    {
        var store = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McServerList", "forge-installers");
        Directory.CreateDirectory(store);

        // Forge：1.7.10 这类老版本的 maven 版本号是 <mc>-<forge>-<mc>，新版本是 <mc>-<forge>，两个都试。
        // NeoForge：版本号本身就是 21.1.255 这种（不含 mc 前缀），装机包叫 neoforge-<ver>-installer.jar。
        var mavenVersions = kind == LoaderKind.NeoForge
            ? [forgeVersion]
            : new[] { $"{gameVersion}-{forgeVersion}", $"{gameVersion}-{forgeVersion}-{gameVersion}" };
        var officialBase = kind == LoaderKind.NeoForge ? OfficialNeoForgeMaven : OfficialMaven;

        foreach (var mavenVersion in mavenVersions)
        {
            var relative = kind == LoaderKind.NeoForge
                ? $"net/neoforged/neoforge/{mavenVersion}/neoforge-{mavenVersion}-installer.jar"
                : $"net/minecraftforge/forge/{mavenVersion}/forge-{mavenVersion}-installer.jar";
            var target = Path.Combine(store, Path.GetFileName(relative));
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                return target;
            }

            var urls = useMirror
                ? new[] { BmclMaven + relative, officialBase + relative }
                : new[] { officialBase + relative, BmclMaven + relative };
            foreach (var url in urls)
            {
                // 边下边写（不要用 ReadAsByteArrayAsync 一次性读进内存）：
                // ① 下载过程中目标目录的字节数会持续增长，App 的卡死看门狗才看得出"有进展"；
                // ② 单个源卡住时用"30 秒没有数据"来判定并拆连接，而不是给整个下载一个总时限。
                if (await TryDownloadToFileAsync(url, target, cancellationToken).ConfigureAwait(false))
                {
                    return target;
                }
            }
        }

        return null;
    }

    /// <summary>把 URL 下到 target（写 .part 再改名），30 秒没有数据就拆掉连接换源。</summary>
    private static async Task<bool> TryDownloadToFileAsync(string url, string target, CancellationToken cancellationToken)
    {
        var temporary = target + ".part";
        HttpResponseMessage? response = null;
        try
        {
            response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var destination = File.Create(temporary);
            var buffer = new byte[81920];
            while (true)
            {
                // 同 GameFileDownloader：有些网络环境下 socket 读不理会取消令牌，超时必须把 response Dispose 掉才解得开
                var readTask = NetCompat.ReadAsyncTask(source, buffer, cancellationToken);
                var winner = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None))
                    .ConfigureAwait(false);
                if (winner != readTask)
                {
                    _ = readTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                    response.Dispose();
                    response = null;
                    throw new IOException($"下载 {url} 时 30 秒没有数据");
                }

                var read = await readTask.ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            destination.Dispose();
            if (new FileInfo(temporary).Length == 0)
            {
                return false;
            }

            NetCompat.MoveFile(temporary, target, overwrite: true);
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
                // 清理失败不影响换源
            }

            return false;
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>按版本 JSON 声明的 Java 要求挑一个本机 Java（找不到返回 null）。</summary>
    private static string? PickJava(string gameRoot, string versionId)
    {
        var required = JavaRuntimeService.RequiredFeatureVersion(gameRoot, versionId);
        var runtimes = new JavaRuntimeService()
            .Detect(deepSearch: false)
            .Where(r => File.Exists(r.JavaPath))
            .ToList();
        // 与启动时同一套规则：完全一致的优先 → 其次"≥ 要求里最小的"；1.13 之前的老版本只认 Java 8
        return JavaRuntimeService.PickBest(runtimes, versionId, required)?.JavaPath;
    }

    private static string GetMinecraftVersion(JsonElement profile, string versionId)
    {
        var declared = GetString(profile, "minecraft");
        if (!string.IsNullOrWhiteSpace(declared))
        {
            return declared!;
        }

        // 老式安装包没有 minecraft 字段，就用版本 JSON 里的 inheritsFrom
        return profile.TryGetProperty("versionInfo", out var versionInfo) &&
               GetString(versionInfo, "inheritsFrom") is { Length: > 0 } inherits
            ? inherits
            : versionId;
    }

    /// <summary>Forge 的 token 替换（照抄官方 <c>json/Util.replaceTokens</c>）：<c>{KEY}</c> 取 data，<c>'字面量'</c> 去引号，<c>\x</c> 转义。</summary>
    internal static string ReplaceTokens(IReadOnlyDictionary<string, string> data, string value)
    {
        var buffer = new StringBuilder();
        for (var x = 0; x < value.Length; x++)
        {
            var c = value[x];
            if (c == '\\')
            {
                if (x == value.Length - 1)
                {
                    throw new InvalidOperationException($"非法的转义：{value}");
                }

                buffer.Append(value[++x]);
            }
            else if (c is '{' or '\'')
            {
                var key = new StringBuilder();
                var closed = false;
                for (var y = x + 1; y <= value.Length; y++)
                {
                    if (y == value.Length)
                    {
                        throw new InvalidOperationException($"未闭合的 {c}：{value}");
                    }

                    var d = value[y];
                    if (d == '\\')
                    {
                        key.Append(value[++y]);
                    }
                    else if (c == '{' && d == '}')
                    {
                        x = y;
                        closed = true;
                        break;
                    }
                    else if (c == '\'' && d == '\'')
                    {
                        x = y;
                        closed = true;
                        break;
                    }
                    else
                    {
                        key.Append(d);
                    }
                }

                if (!closed)
                {
                    throw new InvalidOperationException($"非法的模式：{value}");
                }

                if (c == '\'')
                {
                    buffer.Append(key);
                }
                else if (data.TryGetValue(key.ToString(), out var replacement))
                {
                    buffer.Append(replacement);
                }
                else
                {
                    throw new InvalidOperationException($"非法的模式：{value}（缺少键 {key}）");
                }
            }
            else
            {
                buffer.Append(c);
            }
        }

        return buffer.ToString();
    }

    private static string? ReadMainClass(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var entry = FindEntry(archive, "META-INF/MANIFEST.MF");
        if (entry is null)
        {
            return null;
        }

        foreach (var line in ReadEntryText(entry).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Main-Class:".Length..].Trim();
            }
        }

        return null;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
    {
        var normalized = name.Replace('\\', '/').TrimStart('/');
        return archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/').TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Sha1(string path)
    {
        using var stream = File.OpenRead(path);
        return NetCompat.Sha1Hex(stream);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响安装结果
        }
    }
}

/// <summary>Maven 坐标：<c>group:artifact:version[:classifier][@extension]</c>（与 Forge 官方的 Artifact 一致）。</summary>
internal sealed class MavenArtifact
{
    private string _relativePath = string.Empty;

    public string Group { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string Version { get; private set; } = string.Empty;

    public string? Classifier { get; private set; }

    public string Extension { get; private set; } = "jar";

    public string RelativePath => _relativePath;

    public static bool TryParse(string descriptor, out MavenArtifact artifact)
    {
        artifact = new MavenArtifact();
        var parts = descriptor.Split(':');
        if (parts.Length < 3)
        {
            return false;
        }

        var last = parts.Length - 1;
        var extension = "jar";
        var extensionIndex = parts[last].IndexOf('@');
        if (extensionIndex >= 0)
        {
            extension = parts[last][(extensionIndex + 1)..];
            parts[last] = parts[last][..extensionIndex];
        }

        artifact = new MavenArtifact
        {
            Group = parts[0],
            Name = parts[1],
            Version = parts[2],
            Classifier = parts.Length > 3 ? parts[3] : null,
            Extension = extension,
        };
        var file = artifact.Name + "-" + artifact.Version +
                   (artifact.Classifier is null ? string.Empty : "-" + artifact.Classifier) +
                   "." + artifact.Extension;
        artifact._relativePath = string.Join("/",
            artifact.Group.Replace('.', '/'), artifact.Name, artifact.Version, file);
        return true;
    }

    /// <summary>这个构件在 <c>libraries/</c> 下的绝对路径。</summary>
    public string LocalPath(string gameRoot) =>
        Path.Combine(gameRoot, "libraries", _relativePath.Replace('/', Path.DirectorySeparatorChar));
}
