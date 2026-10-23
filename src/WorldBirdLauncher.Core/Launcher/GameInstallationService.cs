using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// 游戏与加载器的获取与安装。
///
/// **原版与 Fabric 的安装已自研**（2026-10-06，net48 阶段 2）：
/// 文件规划与并发下载走 <see cref="GameFileDownloader"/>（逐文件大小/存在性判定 + 官方↔BMCLAPI 双源），
/// 已安装列表、版本列表、加载器列表都是自己解析的；
/// **Forge 也已自研**（<see cref="ForgeInstallerService"/>，2026-10-06 第二十四轮）——
/// 到这一轮为止，Core 里只有 <c>AccountService</c> 的统一通行证登录还引用 StarLight 的类型。
/// </summary>
public sealed class GameInstallationService
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // 部分镜像（BMCLAPI）会对没有 User-Agent 的请求返回 403
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.1 (github.com/Themeda/WorldBirdLauncher)");
        return client;
    }

    private const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";
    private const string BmclFabric = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/";
    private const string OfficialFabric = "https://meta.fabricmc.net/v2/versions/loader/";
    private const string BmclForge = "https://bmclapi2.bangbang93.com/forge/minecraft/";
    private const string OfficialForge = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string OfficialFabricProfile = "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json";
    private const string BmclFabricProfile = "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{0}/{1}/profile/json";
    private const string OfficialQuilt = "https://meta.quiltmc.org/v3/versions/loader/";
    private const string OfficialQuiltProfile = "https://meta.quiltmc.org/v3/versions/loader/{0}/{1}/profile/json";
    private const string BmclNeoForgeList = "https://bmclapi2.bangbang93.com/neoforge/list/";
    private const string OfficialNeoForgeVersions = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";

    /// <summary>是否使用 BMCLAPI 镜像（国内推荐）。</summary>
    public bool UseMirror { get; private set; } = true;

    public void SetDownloadSource(bool useMirror)
    {
        UseMirror = useMirror;
    }

    /// <summary>
    /// 列出本地已安装的版本（自研：直接扫 <c>versions\*\</c>，不再走 StarLight 的 <c>GameCoreUtil</c>）。
    /// 版本 id 一律取**目录名**——启动、重命名、完整性校验全都按目录名定位，取 JSON 里的 id 反而会不一致。
    /// </summary>
    public IReadOnlyList<GameVersionInfo> GetInstalledVersions(string gameRoot)
    {
        var result = new List<GameVersionInfo>();
        var versionsRoot = Path.Combine(gameRoot, "versions");
        if (!Directory.Exists(versionsRoot))
        {
            return result;
        }

        foreach (var directory in Directory.GetDirectories(versionsRoot))
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var jsonPath = Path.Combine(directory, id + ".json");
            if (File.Exists(jsonPath))
            {
                var type = TryReadVersionType(jsonPath);
                result.Add(new GameVersionInfo(
                    id,
                    string.IsNullOrWhiteSpace(type) ? id : $"{id}（{type}）",
                    VersionSource.本地已安装,
                    type));
                continue;
            }

            // 「只有 jar、没有同名版本 JSON」的残缺目录：它们启动不了，只当提示（例如手动丢进去的 OptiFine 安装器）
            try
            {
                if (!Directory.EnumerateFiles(directory, "*.jar").Any())
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            result.Add(new GameVersionInfo(
                id,
                $"{id}（不完整：缺少版本 JSON）",
                VersionSource.本地已安装,
                "incomplete")
            {
                IsUsable = false,
            });
        }

        return result.OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>读版本 JSON 的 <c>type</c>（release / snapshot / old_beta…）；读不到返回 null。</summary>
    private static string? TryReadVersionType(string jsonPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>从官方或镜像获取可下载的版本列表。</summary>
    public async Task<IReadOnlyList<GameVersionInfo>> GetRemoteVersionsAsync(CancellationToken cancellationToken = default)
    {
        var json = await GetWithFallbackAsync(
            UseMirror ? BmclManifest : OfficialManifest,
            UseMirror ? OfficialManifest : BmclManifest,
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var result = new List<GameVersionInfo>();
        if (!document.RootElement.TryGetProperty("versions", out var versions))
        {
            return result;
        }

        foreach (var item in versions.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            var type = item.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new GameVersionInfo(id!, type == "release" ? id! : $"{id}（{type}）", VersionSource.远程可下载, type));
        }

        return result;
    }

    /// <summary>获取指定游戏版本的 Fabric 加载器版本列表。</summary>
    public async Task<IReadOnlyList<string>> GetFabricLoaderVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = UseMirror ? BmclFabric : OfficialFabric;
        var otherUrl = UseMirror ? OfficialFabric : BmclFabric;
        var json = await GetWithFallbackAsync(
            $"{baseUrl}{gameVersion}",
            $"{otherUrl}{gameVersion}",
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("version", out var version))
            {
                var value = version.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    result.Add(value!);
                }
            }
        }

        return result;
    }

    /// <summary>获取指定游戏版本的 Forge 版本列表。</summary>
    public async Task<IReadOnlyList<string>> GetForgeVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        if (UseMirror)
        {
            var json = await GetWithFallbackAsync(
                $"{BmclForge}{gameVersion}",
                $"https://bmclapi2.bangbang93.com/forge/minecraft/{gameVersion}",
                cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var versions = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("version", out var version))
                {
                    var value = version.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        versions.Add(value!);
                    }
                }
            }

            return versions;
        }

        // 官方接口只提供推荐/最新版本，作为无镜像时的兜底
        var promotions = await Http.GetStringAsync(OfficialForge, cancellationToken).ConfigureAwait(false);
        using var promoDocument = JsonDocument.Parse(promotions);
        var result = new List<string>();
        if (promoDocument.RootElement.TryGetProperty("promos", out var promos))
        {
            foreach (var property in promos.EnumerateObject())
            {
                if (property.Name.StartsWith($"{gameVersion}-", StringComparison.Ordinal))
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value!);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 获取指定游戏版本的 NeoForge 版本列表。
    /// 官方 maven 的接口返回**全部**版本（要按 mc 前缀过滤），BMCLAPI 的 `/neoforge/list/&lt;mc&gt;` 直接给该版本的；
    /// 而且官方 maven 在部分网络下完全连不上（本机就是），所以两个源都试。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetNeoForgeVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await GetWithFallbackAsync(
                $"{BmclNeoForgeList}{Uri.EscapeDataString(gameVersion)}",
                OfficialNeoForgeVersions,
                cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var result = new List<string>();

            if (document.RootElement.ValueKind == JsonValueKind.Array) // BMCLAPI：该版本的列表
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("version", out var version) && version.GetString() is { Length: > 0 } value)
                    {
                        result.Add(value);
                    }
                }
            }
            else if (document.RootElement.TryGetProperty("versions", out var versions)) // 官方：全部版本
            {
                var prefix = NeoForgeVersionPrefix(gameVersion);
                foreach (var item in versions.EnumerateArray())
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value) && value!.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        result.Add(value!);
                    }
                }
            }

            return result.Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>NeoForge 版本号前缀：1.21.1 → <c>21.1.</c>；1.21 → <c>21.0.</c>（NeoForge 的版本号里带 mc 小版本）。</summary>
    internal static string NeoForgeVersionPrefix(string gameVersion)
    {
        var value = gameVersion.Trim();
        if (value.StartsWith("1.", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        return value.Contains('.') ? value + "." : value + ".0.";
    }

    /// <summary>获取指定游戏版本的 Quilt 加载器版本列表（官方 meta，BMCLAPI 没有 quilt-meta 镜像）。</summary>
    public async Task<IReadOnlyList<string>> GetQuiltLoaderVersionsAsync(
        string gameVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await Http
                .GetStringAsync($"{OfficialQuilt}{Uri.EscapeDataString(gameVersion)}", cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var result = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("loader", out var loader) &&
                    loader.TryGetProperty("version", out var version) &&
                    version.GetString() is { Length: > 0 } value)
                {
                    result.Add(value);
                }
            }

            return result;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>安装原版游戏。</summary>
    public async Task<InstallOutcome> InstallVanillaAsync(
        string gameVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await InstallVanillaCoreAsync(gameVersion, gameRoot, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 统一安装入口：原版 / Forge / Fabric，失败时自动换源重试一次（BMCLAPI ↔ 官方）。
    ///
    /// 换源重试非常必要：镜像对个别文件返回 403/404 时，换成官方源往往就能过。
    /// </summary>
    public async Task<InstallOutcome> InstallWithFallbackAsync(
        string gameVersion,
        string loader,
        string? loaderVersion,
        string gameRoot,
        bool useMirror,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 先把客户端 jar 自己放好：StarLight 在"官方源"下会去 legacy 的
        // https://launcher.mojang.com/version/<id>/client 取客户端，现代版本那里必然 404
        // （The specified blob does not exist.）。放好之后 StarLight 检测到文件存在就会跳过。
        await EnsureClientJarAsync(gameVersion, gameRoot, progress, cancellationToken).ConfigureAwait(false);

        // 每次尝试都挂一个"卡死看门狗"：StarLight 的下载没有超时，遇到挂住的连接会永远停住，
        // 这里在长时间没有任何文件变化时中断本次尝试（不算用户取消），交给下面的换源重试。
        async Task<InstallOutcome> RunGuardedAsync()
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var guard = new DownloadStallGuard(
                gameRoot,
                attempt,
                message => progress?.Report(new InstallProgress(message, -1)),
                // 安装包 / 登录注入器都下在 %APPDATA%\WorldBirdLauncher 下，也要算进"有没有进展"
                [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorldBirdLauncher")]);
            guard.Start();
            try
            {
                return loader switch
                {
                    "NeoForge" when !string.IsNullOrWhiteSpace(loaderVersion) =>
                        await new ForgeInstallerService().InstallAsync(
                            gameVersion, loaderVersion!, gameRoot, UseMirror, progress, attempt.Token,
                            ForgeInstallerService.LoaderKind.NeoForge).ConfigureAwait(false),
                    "Quilt" when !string.IsNullOrWhiteSpace(loaderVersion) =>
                        await InstallProfileLoaderAsync(
                            gameVersion, loaderVersion!, gameRoot, progress, attempt.Token, QuiltProfile).ConfigureAwait(false),
                    "Forge" when !string.IsNullOrWhiteSpace(loaderVersion) =>
                        await InstallForgeAsync(gameVersion, loaderVersion!, gameRoot, progress, attempt.Token).ConfigureAwait(false),
                    "Fabric" when !string.IsNullOrWhiteSpace(loaderVersion) =>
                        await InstallFabricAsync(gameVersion, loaderVersion!, gameRoot, progress, attempt.Token).ConfigureAwait(false),
                    "原版" or "" or null =>
                        await InstallVanillaCoreAsync(gameVersion, gameRoot, progress, attempt.Token).ConfigureAwait(false),
                    // 不认识的加载器**不能**再默默当原版装（装出来是个没有加载器的版本，用户还以为装上了）
                    _ => InstallOutcome.Fail($"暂不支持自动安装 {loader} 加载器。"),
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return InstallOutcome.Fail("下载长时间没有进展，已中断本次尝试（可能是下载连接被挂住），正在换源重试…");
            }
        }

        SetDownloadSource(useMirror);
        var result = await RunGuardedAsync().ConfigureAwait(false);
        if (result.Success)
        {
            return result;
        }

        // 换源前稍等一下：BMCLAPI 请求过密会返回 403（限流），等几秒再试往往就过了
        await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        await EnsureClientJarAsync(gameVersion, gameRoot, progress, cancellationToken).ConfigureAwait(false);

        SetDownloadSource(!useMirror);
        try
        {
            var retry = await RunGuardedAsync().ConfigureAwait(false);
            if (retry.Success)
            {
                return retry;
            }

            // 两个源都失败时，再给首选源一次机会（限流通常是短暂的）
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            SetDownloadSource(useMirror);
            var last = await RunGuardedAsync().ConfigureAwait(false);
            return last.Success ? last : retry;
        }
        finally
        {
            SetDownloadSource(useMirror);
        }
    }

    /// <summary>
    /// 用版本 JSON 里的 <c>downloads.client.url</c>（官方 piston-data，永远正确）自己下载客户端 jar。
    /// 失败不抛异常——交给后续安装流程去报错。
    /// </summary>
    public async Task<bool> EnsureClientJarAsync(
        string gameVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var versionDirectory = Path.Combine(gameRoot, "versions", gameVersion);
            var target = Path.Combine(versionDirectory, gameVersion + ".jar");
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                return true;
            }

            var manifest = await GetWithFallbackAsync(BmclManifest, OfficialManifest, cancellationToken).ConfigureAwait(false);
            using var manifestDocument = JsonDocument.Parse(manifest);
            var versionUrl = manifestDocument.RootElement.GetProperty("versions").EnumerateArray()
                .Where(v => string.Equals(v.GetProperty("id").GetString(), gameVersion, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.GetProperty("url").GetString())
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            if (versionUrl is null)
            {
                return false;
            }

            var versionJson = await GetWithFallbackAsync(versionUrl, versionUrl, cancellationToken).ConfigureAwait(false);
            using var versionDocument = JsonDocument.Parse(versionJson);
            var clientUrl = versionDocument.RootElement.GetProperty("downloads").GetProperty("client").GetProperty("url").GetString();
            if (string.IsNullOrWhiteSpace(clientUrl))
            {
                return false;
            }

            progress?.Report(new InstallProgress($"正在准备客户端文件（{gameVersion}）", 15));
            Directory.CreateDirectory(versionDirectory);
            using var response = await Http.GetAsync(clientUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var destination = File.Create(target);
            await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<InstallOutcome> InstallVanillaCoreAsync(
        string gameVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 原版安装现在**完全自研**：版本 JSON / 客户端 jar / 全部库 / 资源索引 / 每个资源对象都由
        // GameFileDownloader 按官方版本 JSON 规划出来，再并发 + 双源下载（逐文件判断"是否已是最新"）。
        // natives 不在这里解压——启动时由 GameArgumentsBuilder.ExtractNatives 幂等补齐，
        // 安装与启动共用同一份目录规则，不会出现"解到没人读的目录"。
        var result = await new GameFileDownloader()
            .DownloadAsync(gameVersion, gameRoot, UseMirror, 8, progress, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? InstallOutcome.Ok(
                gameVersion,
                result.Downloaded > 0
                    ? $"安装完成：下载 {result.Downloaded} 个文件（跳过 {result.Skipped} 个已存在的）。"
                    : "所需文件已全部就绪。")
            : InstallOutcome.Fail(result.Message);
    }

    /// <summary>
    /// 「官方 launcher profile 型」加载器（Fabric / Quilt）的档案来源。
    /// 两者的档案结构同源（<c>inheritsFrom</c> / <c>mainClass</c> / <c>libraries</c>），区别只有站点与目录名前缀。
    /// </summary>
    private sealed record ProfileLoader(string Name, string VersionIdInfix, string OfficialProfileUrl, string? MirrorProfileUrl);

    private static readonly ProfileLoader FabricProfile =
        new("Fabric", "fabric-loader_", OfficialFabricProfile, BmclFabricProfile);

    /// <summary>Quilt：官方 meta 可用，但 **BMCLAPI 没有 quilt-meta 镜像**（实测 404），所以只有官方源。</summary>
    private static readonly ProfileLoader QuiltProfile =
        new("Quilt", "quilt-loader_", OfficialQuiltProfile, null);

    /// <summary>
    /// 安装 Fabric 加载器（自研，不再用 StarLight 的 <c>FabricInstaller</c>）。
    /// 步骤见 <see cref="InstallProfileLoaderAsync"/>。
    /// </summary>
    public async Task<InstallOutcome> InstallFabricAsync(
        string gameVersion,
        string fabricLoaderVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await InstallProfileLoaderAsync(
                gameVersion, fabricLoaderVersion, gameRoot, progress, cancellationToken, FabricProfile)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 装「官方 launcher profile 型」加载器：① 父版本原版没装就先补装；② 取官方 profile
    /// （<c>meta.fabricmc.net</c> / <c>meta.quiltmc.org</c>，Fabric 还能走 BMCLAPI 的 fabric-meta）；
    /// ③ 把 <c>id</c> 改成我们的目录名后落盘；④ 用 <see cref="GameFileDownloader"/> 把 profile 里的库拉齐。
    /// </summary>
    private async Task<InstallOutcome> InstallProfileLoaderAsync(
        string gameVersion,
        string loaderVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken,
        ProfileLoader descriptor)
    {
        var versionId = $"{gameVersion}-{descriptor.VersionIdInfix}{loaderVersion}";

        if (!IsVersionInstalled(gameRoot, gameVersion))
        {
            progress?.Report(new InstallProgress($"{descriptor.Name} 需要先安装原版 {gameVersion}", 5));
            var parent = await InstallVanillaCoreAsync(gameVersion, gameRoot, progress, cancellationToken).ConfigureAwait(false);
            if (!parent.Success)
            {
                return InstallOutcome.Fail(
                    $"{descriptor.Name} 需要先安装原版 {gameVersion}，但原版安装失败：{parent.Message}");
            }
        }

        progress?.Report(new InstallProgress($"正在获取 {descriptor.Name} {loaderVersion} 的安装档案", 30));
        var profile = await GetProfileAsync(descriptor, gameVersion, loaderVersion, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return InstallOutcome.Fail($"无法获取 {descriptor.Name} 安装档案（{descriptor.Name} 的 meta 源不可用）。");
        }

        var directory = Path.Combine(gameRoot, "versions", versionId);
        Directory.CreateDirectory(directory);
        await NetCompat.WriteAllTextAsync(
                Path.Combine(directory, versionId + ".json"),
                RewriteFabricProfileJson(profile, versionId),
                new UTF8Encoding(false),
                cancellationToken)
            .ConfigureAwait(false);

        var result = await new GameFileDownloader()
            .DownloadAsync(versionId, gameRoot, UseMirror, 8, progress, cancellationToken)
            .ConfigureAwait(false);
        return result.Success
            ? InstallOutcome.Ok(versionId, $"{descriptor.Name} 安装完成。")
            : InstallOutcome.Fail(result.Message);
    }

    /// <summary>Fabric 版本的目录名约定：<c>&lt;游戏版本&gt;-fabric-loader_&lt;加载器版本&gt;</c>（与历史目录名一致）。</summary>
    public static string FabricVersionId(string gameVersion, string fabricLoaderVersion) =>
        $"{gameVersion}-fabric-loader_{fabricLoaderVersion}";

    /// <summary>Quilt 版本的目录名约定：<c>&lt;游戏版本&gt;-quilt-loader_&lt;加载器版本&gt;</c>。</summary>
    public static string QuiltVersionId(string gameVersion, string quiltLoaderVersion) =>
        $"{gameVersion}-quilt-loader_{quiltLoaderVersion}";

    /// <summary>
    /// 把官方 Fabric profile JSON 的 <c>id</c> 改成我们的目录名（其它字段原样保留，
    /// 尤其是 <c>inheritsFrom</c> 必须继续指向原版）。
    /// </summary>
    internal static string RewriteFabricProfileJson(string profileJson, string versionId)
    {
        var node = JsonNode.Parse(profileJson) ?? throw new InvalidOperationException("Fabric 安装档案不是合法 JSON。");
        node["id"] = versionId;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>取官方 launcher profile，首选源失败自动换镜像（没有镜像就只用官方）。</summary>
    private static async Task<string?> GetProfileAsync(
        ProfileLoader descriptor,
        string gameVersion,
        string loaderVersion,
        CancellationToken cancellationToken)
    {
        var official = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            descriptor.OfficialProfileUrl,
            Uri.EscapeDataString(gameVersion),
            Uri.EscapeDataString(loaderVersion));

        try
        {
            if (descriptor.MirrorProfileUrl is null)
            {
                return await Http.GetStringAsync(official, cancellationToken).ConfigureAwait(false);
            }

            var mirror = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                descriptor.MirrorProfileUrl,
                Uri.EscapeDataString(gameVersion),
                Uri.EscapeDataString(loaderVersion));
            return await GetWithFallbackAsync(official, mirror, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>这个版本是否已经装好（版本 JSON + 客户端 jar 都在）。</summary>
    private static bool IsVersionInstalled(string gameRoot, string versionId)
    {
        var directory = Path.Combine(gameRoot, "versions", versionId);
        return File.Exists(Path.Combine(directory, versionId + ".json")) &&
               File.Exists(Path.Combine(directory, versionId + ".jar"));
    }

    /// <summary>
    /// 安装 Forge 加载器（自研，见 <see cref="ForgeInstallerService"/>）。
    /// 老版本（1.7.10 等）走 install + versionInfo，新版本跑 processors，两者都在那边实现。
    /// </summary>
    public async Task<InstallOutcome> InstallForgeAsync(
        string gameVersion,
        string forgeVersion,
        string gameRoot,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await new ForgeInstallerService()
            .InstallAsync(gameVersion, forgeVersion, gameRoot, UseMirror, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 先请求首选地址，失败（403/404/超时/限流）自动换备用地址重试。
    /// BMCLAPI 在请求过密时会返回 403，这一层能显著减少"下载报 forbidden"。
    /// </summary>
    private static async Task<string> GetWithFallbackAsync(string primary, string fallback, CancellationToken cancellationToken)
    {
        try
        {
            return await Http.GetStringAsync(primary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await Http.GetStringAsync(fallback, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 把已安装的版本改成自定义目录名（用户可给同一游戏版本装多份）。
    /// 会一并改名：版本目录、<c>&lt;id&gt;.jar</c>、<c>&lt;id&gt;.json</c>、<c>&lt;id&gt;-natives</c>，并改写 JSON 里的 id。
    /// </summary>
    public static bool TryRenameVersion(string gameRoot, string oldId, string newId, out string message)
    {
        try
        {
            var versionsRoot = Path.Combine(gameRoot, "versions");
            var oldDirectory = Path.Combine(versionsRoot, oldId);
            var newDirectory = Path.Combine(versionsRoot, newId);
            if (!Directory.Exists(oldDirectory))
            {
                message = $"找不到版本目录：{oldId}";
                return false;
            }

            if (Directory.Exists(newDirectory))
            {
                message = $"目标版本名已存在：{newId}";
                return false;
            }

            Directory.Move(oldDirectory, newDirectory);

            foreach (var (oldName, newName) in new[]
                     {
                         ($"{oldId}.jar", $"{newId}.jar"),
                         ($"{oldId}.json", $"{newId}.json"),
                         ($"{oldId}-natives", $"{newId}-natives"),
                     })
            {
                var oldPath = Path.Combine(newDirectory, oldName);
                if (!File.Exists(oldPath) && !Directory.Exists(oldPath))
                {
                    continue;
                }

                var newPath = Path.Combine(newDirectory, newName);
                if (Directory.Exists(oldPath))
                {
                    Directory.Move(oldPath, newPath);
                }
                else
                {
                    File.Move(oldPath, newPath);
                }
            }

            var jsonPath = Path.Combine(newDirectory, $"{newId}.json");
            if (File.Exists(jsonPath))
            {
                var json = File.ReadAllText(jsonPath);
                json = System.Text.RegularExpressions.Regex.Replace(
                    json,
                    "\"id\"\\s*:\\s*\"" + System.Text.RegularExpressions.Regex.Escape(oldId) + "\"",
                    $"\"id\": \"{newId}\"");
                File.WriteAllText(jsonPath, json, new System.Text.UTF8Encoding(false));
            }

            message = $"已重命名为 {newId}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"重命名失败：{ex.Message}";
            return false;
        }
    }
}
