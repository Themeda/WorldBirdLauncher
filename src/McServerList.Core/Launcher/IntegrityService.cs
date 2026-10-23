using System.Security.Cryptography;
using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>一处完整性问题。</summary>
public sealed record IntegrityIssue(string Category, string Target, string Reason);

/// <summary>日志行类型（供界面区分颜色/图标）。</summary>
public enum IntegrityLogKind
{
    /// <summary>分类标题，如「客户端 jar」。</summary>
    Header,
    /// <summary>提示信息（跳过、统计）。</summary>
    Info,
    /// <summary>校验通过。</summary>
    Pass,
    /// <summary>校验失败（缺失 / 哈希不符）。</summary>
    Fail,
}

/// <summary>一行校验日志。</summary>
public sealed record IntegrityLogEntry(IntegrityLogKind Kind, string Text);

/// <summary>完整性校验结果。</summary>
public sealed record IntegrityReport(
    int Checked,
    int Passed,
    int Missing,
    int HashMismatch,
    int Skipped,
    int OptionalSkipped,
    IReadOnlyList<IntegrityIssue> Issues)
{
    public bool IsHealthy => Missing == 0 && HashMismatch == 0;

    public string Summary =>
        $"共校验 {Checked} 个文件：通过 {Passed}，缺失 {Missing}，哈希不符 {HashMismatch}"
        + (Skipped > 0 ? $"，跳过 {Skipped} 个不适用于当前系统的文件" : string.Empty)
        + (OptionalSkipped > 0 ? $"，可选包缺失 {OptionalSkipped} 个（不影响启动）" : string.Empty);
}

/// <summary>
/// 游戏版本完整性校验：不只检查文件是否存在，还按版本 JSON 里的 sha1 校验内容
/// （客户端 jar、全部库文件、以及资源索引里的每个资源对象）。
/// 会按 <c>rules</c> 过滤掉不适用于当前系统（Windows/Linux/macOS）的文件，
/// 并沿 <c>inheritsFrom</c> 把父版本（原版）的库一起纳入校验。
/// </summary>
public sealed class IntegrityService
{
    private const int MaxIssues = 500;

    /// <summary>
    /// 「可选依赖」：Mojang 早已移除、游戏不装也能正常启动的包。
    /// 实测 1.7.10 缺 twitch-platform 时游戏照常进入主界面；1.13 起官方 JSON 里已彻底删掉这些包。
    /// </summary>
    private static readonly string[] OptionalPackagePrefixes = ["tv.twitch:"];

    private static readonly string CurrentOsName =
        NetCompat.IsWindows() ? "windows" :
        NetCompat.IsMacOS() ? "osx" : "linux";

    public async Task<IntegrityReport> CheckAsync(
        string gameRoot,
        string versionId,
        IProgress<IntegrityLogEntry>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<IntegrityIssue>();
        var checkedCount = 0;
        var passed = 0;
        var missing = 0;
        var mismatch = 0;
        var skipped = 0;
        var optionalSkipped = 0;

        var chain = await LoadVersionChainAsync(gameRoot, versionId, cancellationToken).ConfigureAwait(false);
        if (chain is null)
        {
            return new IntegrityReport(0, 0, 1, 0, 0, 0, [new IntegrityIssue("版本", versionId, "版本 JSON 不存在")]);
        }

        // 1) 客户端 jar
        progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Header, "客户端 jar"));
        // 客户端 jar 属于"声明了 downloads.client 的那个版本"——Fabric/Forge 这类加载器版本自己没有 jar，
        // 用的是父版本（原版）的 jar（启动器也是这么解析的），所以这里要沿继承链找，别再误报"文件缺失"。
        var jarVersionId = chain.ClientJarVersionId;
        var jarPath = Path.Combine(gameRoot, "versions", jarVersionId, jarVersionId + ".jar");
        var jarLabel = string.Equals(jarVersionId, versionId, StringComparison.OrdinalIgnoreCase)
            ? versionId + ".jar"
            : $"{jarVersionId}.jar（父版本的客户端 jar）";
        checkedCount++;
        if (await VerifyAsync(jarPath, chain.ClientSha1, cancellationToken).ConfigureAwait(false) == VerifyResult.Passed)
        {
            passed++;
            progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Pass, jarLabel));
        }
        else
        {
            var (kind, reason) = await ClassifyAsync(jarPath, chain.ClientSha1, cancellationToken).ConfigureAwait(false);
            if (kind == VerifyResult.Missing)
            {
                missing++;
                progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Fail, jarLabel + " —— 文件缺失"));
            }
            else
            {
                mismatch++;
                progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Fail, jarLabel + " —— SHA-1 与版本 JSON 不一致"));
            }

            AddIssue(issues, "客户端", jarLabel, reason);
        }

        // 2) 库文件（含父版本；按 rules 过滤掉其它系统专用的）
        progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Header, "库文件"));
        var libraryCount = 0;
        foreach (var library in chain.Libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!MinecraftLibraryRules.IsAllowedForThisSystem(library))
            {
                skipped++;
                continue;
            }

            // 一个库可能要两个文件：主 artifact + 当前系统的 natives 包（1.16.x/1.17.x 的官方 JSON
            // 用"同名两条"表达），两个都要校验，否则 natives 缺失查不出来（表现为启动时报 lwjgl.dll 找不到）
            foreach (var (relativePath, _, sha1, _) in MinecraftLibraryRules.ResolveFiles(library))
            {
            libraryCount++;
            var path = Path.Combine(gameRoot, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));

            // 可选包（如 Twitch 直播集成）缺失不算问题，但存在时照常校验哈希
            if (!File.Exists(path) && IsOptionalPackage(library))
            {
                optionalSkipped++;
                progress?.Report(new IntegrityLogEntry(
                    IntegrityLogKind.Info, $"可选包未安装，已跳过：{relativePath}"));
                continue;
            }

            checkedCount++;
            var result = await VerifyAsync(path, sha1, cancellationToken).ConfigureAwait(false);
            if (result == VerifyResult.Passed)
            {
                passed++;
                progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Pass, relativePath));
            }
            else
            {
                var label = result == VerifyResult.Missing ? "文件缺失" : "SHA-1 与版本 JSON 不一致";
                if (result == VerifyResult.Missing)
                {
                    missing++;
                }
                else
                {
                    mismatch++;
                }

                progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Fail, $"{relativePath} —— {label}"));
                AddIssue(issues, "库文件", relativePath, label);
            }
            }
        }

        if (skipped > 0)
        {
            progress?.Report(new IntegrityLogEntry(
                IntegrityLogKind.Info, $"已跳过 {skipped} 个不适用于当前系统（{CurrentOsName}）的库文件"));
        }

        if (optionalSkipped > 0)
        {
            progress?.Report(new IntegrityLogEntry(
                IntegrityLogKind.Info, $"已跳过 {optionalSkipped} 个未安装的可选包（如 Twitch 直播集成，不影响启动）"));
        }

        // 3) 资源文件（按本地资源索引里的 hash 校验）
        progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Header, "资源文件"));
        if (!string.IsNullOrWhiteSpace(chain.AssetIndexId))
        {
            var indexFile = Path.Combine(gameRoot, "assets", "indexes", chain.AssetIndexId + ".json");
            if (File.Exists(indexFile))
            {
                using var indexDocument = JsonDocument.Parse(
                    await NetCompat.ReadAllTextAsync(indexFile, cancellationToken).ConfigureAwait(false));
                if (indexDocument.RootElement.TryGetProperty("objects", out var objects) &&
                    objects.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in objects.EnumerateObject())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = property.Name;
                        var hash = property.Value.TryGetProperty("hash", out var hashElement) ? hashElement.GetString() : null;
                        if (string.IsNullOrWhiteSpace(hash) || hash!.Length < 2)
                        {
                            continue;
                        }

                        var path = Path.Combine(gameRoot, "assets", "objects", hash[..2], hash);
                        checkedCount++;
                        var result = await VerifyAsync(path, hash, cancellationToken).ConfigureAwait(false);
                        if (result == VerifyResult.Passed)
                        {
                            passed++;
                            progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Pass, name));
                        }
                        else
                        {
                            var label = result == VerifyResult.Missing ? "文件缺失" : "SHA-1 不匹配";
                            if (result == VerifyResult.Missing)
                            {
                                missing++;
                            }
                            else
                            {
                                mismatch++;
                            }

                            progress?.Report(new IntegrityLogEntry(IntegrityLogKind.Fail, $"{name} —— {label}"));
                            AddIssue(issues, "资源", name, label);
                        }
                    }
                }
            }
            else
            {
                progress?.Report(new IntegrityLogEntry(
                    IntegrityLogKind.Info, $"本地缺少资源索引 {chain.AssetIndexId}.json，已跳过资源校验（重装该版本可补全）"));
            }
        }

        return new IntegrityReport(checkedCount, passed, missing, mismatch, skipped, optionalSkipped, issues);
    }

    private static void AddIssue(List<IntegrityIssue> issues, string category, string target, string reason)
    {
        if (issues.Count < MaxIssues)
        {
            issues.Add(new IntegrityIssue(category, target, reason));
        }
    }

    /// <summary>
    /// 版本 JSON 链：自己的 + 沿 inheritsFrom 追溯到的父版本（原版）的库。
    /// <paramref name="ClientJarVersionId"/> 是"客户端 jar 属于哪个版本"——加载器版本没有自己的 jar，用的是父版本的。
    /// </summary>
    private sealed record VersionChain(
        string? ClientSha1,
        string? AssetIndexId,
        IReadOnlyList<JsonElement> Libraries,
        string ClientJarVersionId);

    private static async Task<VersionChain?> LoadVersionChainAsync(
        string gameRoot,
        string versionId,
        CancellationToken cancellationToken)
    {
        var libraries = new List<JsonElement>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        string? clientSha1 = null;
        string? assetIndexId = null;
        string? clientJarVersionId = null;

        var currentId = versionId;
        var depth = 0;
        while (!string.IsNullOrWhiteSpace(currentId) && depth++ < 8)
        {
            var versionJsonPath = Path.Combine(gameRoot, "versions", currentId, currentId + ".json");
            if (!File.Exists(versionJsonPath))
            {
                return depth == 1 ? null : new VersionChain(clientSha1, assetIndexId, libraries, clientJarVersionId ?? versionId);
            }

            using var document = JsonDocument.Parse(
                await NetCompat.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;

            if (clientSha1 is null && TryGetClientSha1(root) is { Length: > 0 } sha1)
            {
                clientSha1 = sha1;
                clientJarVersionId ??= currentId;
            }
            assetIndexId ??= root.TryGetProperty("assetIndex", out var assetIndex) &&
                              assetIndex.TryGetProperty("id", out var idElement)
                ? idElement.GetString()
                : null;

            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            {
                foreach (var library in libs.EnumerateArray())
                {
                    var name = library.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    // 子版本里已经声明过的同名库优先，父版本的不再重复校验
                    if (!string.IsNullOrWhiteSpace(name) && !seenNames.Add(name!))
                    {
                        continue;
                    }

                    libraries.Add(library.Clone());
                }
            }

            currentId = root.TryGetProperty("inheritsFrom", out var inheritsFrom) ? inheritsFrom.GetString() : null;
        }

        return new VersionChain(clientSha1, assetIndexId, libraries, clientJarVersionId ?? versionId);
    }

    private static string? TryGetClientSha1(JsonElement root) =>
        root.TryGetProperty("downloads", out var downloads) &&
        downloads.TryGetProperty("client", out var client) &&
        client.TryGetProperty("sha1", out var sha1Element)
            ? sha1Element.GetString()
            : null;

    /// <summary>是不是「可选依赖」（缺失不影响游戏启动）。</summary>
    private static bool IsOptionalPackage(JsonElement library) =>
        library.TryGetProperty("name", out var name) &&
        name.GetString() is { Length: > 0 } package &&
        OptionalPackagePrefixes.Any(p => package.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private enum VerifyResult
    {
        Passed,
        Missing,
        Mismatch,
    }

    private static async Task<(VerifyResult Kind, string Reason)> ClassifyAsync(
        string path,
        string? expectedSha1,
        CancellationToken cancellationToken)
    {
        var result = await VerifyAsync(path, expectedSha1, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            VerifyResult.Missing => (VerifyResult.Missing, "文件缺失"),
            VerifyResult.Mismatch => (VerifyResult.Mismatch, "SHA-1 与版本 JSON 不一致"),
            _ => (VerifyResult.Passed, string.Empty),
        };
    }

    private static async Task<VerifyResult> VerifyAsync(string path, string? expectedSha1, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return VerifyResult.Missing;
        }

        if (string.IsNullOrWhiteSpace(expectedSha1))
        {
            return VerifyResult.Passed; // 没有期望哈希时只校验存在性
        }

        using var stream = File.OpenRead(path);
        var actual = await NetCompat.Sha1HexAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase)
            ? VerifyResult.Passed
            : VerifyResult.Mismatch;
    }
}
