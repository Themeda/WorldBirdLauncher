using System.Diagnostics;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>OptiFine 自动安装结果。</summary>
public sealed record OptiFineInstallOutcome(bool Success, string? VersionId, string Message);

/// <summary>
/// 自动安装 OptiFine：调用 OptiFine 官方安装器自带的 <c>optifine.Installer.doInstall(File)</c>。
///
/// 官方安装器（`OptiFine_x.jar`）自带这个方法，但它的 <c>main()</c> 只认识
/// <c>%APPDATA%\.minecraft</c>（工作目录写死），所以这里用内嵌的
/// <c>OptiFineHeadless</c> 转发类把"目标游戏目录"传进去。
///
/// 装完会得到 <c>versions/&lt;OptiFine 版本&gt;/{json,jar}</c> 与
/// <c>libraries/optifine/OptiFine/&lt;版本&gt;/OptiFine-&lt;版本&gt;.jar</c>；
/// 版本 JSON 里还引用了 <c>net.minecraft:launchwrapper:1.12</c>（官方启动器会自动补），
/// 这里要自己补下载，否则启动会缺 launchwrapper。
/// </summary>
public sealed class OptiFineInstallerService
{
    private const string HelperResource = "WorldBirdLauncher.Core.Launcher.OptiFine.OptiFineHeadless.class";

    private static readonly string[] LibraryMirrors =
    [
        "https://libraries.minecraft.net/",
        "https://bmclapi2.bangbang93.com/maven/",
    ];

    public async Task<OptiFineInstallOutcome> InstallAsync(
        string gameRoot,
        string javaPath,
        string installerJar,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(javaPath))
        {
            return new OptiFineInstallOutcome(false, null, $"Java 不存在：{javaPath}");
        }

        if (!File.Exists(installerJar))
        {
            return new OptiFineInstallOutcome(false, null, $"OptiFine 安装器不存在：{installerJar}");
        }

        var versionsRoot = Path.Combine(gameRoot, "versions");
        Directory.CreateDirectory(versionsRoot);
        var before = GetVersionIds(versionsRoot);

        var helperDirectory = Path.Combine(Path.GetTempPath(), "WorldBirdLauncher", "optifine-helper");
        Directory.CreateDirectory(helperDirectory);
        var helperPath = Path.Combine(helperDirectory, "OptiFineHeadless.class");
        await ExtractHelperAsync(helperPath, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress("正在运行 OptiFine 安装器", 35));

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            Arguments = $"-cp \"{helperDirectory};{installerJar}\" OptiFineHeadless \"{gameRoot}\"",
            WorkingDirectory = gameRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new List<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (output)
                {
                    output.Add(e.Data!);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (output)
                {
                    output.Add("[错误] " + e.Data);
                }
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await NetCompat.WaitForExitAsync(process, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    NetCompat.KillProcessTree(process);
                }
            }
            catch (Exception)
            {
                // 取消时杀进程失败可以忽略
            }

            throw;
        }

        // 官方安装器最后一步会去改官方启动器的 launcher_profiles.json，找不到就抛 "QUIET"（退出码 1），
        // 但此时版本目录与库文件都已经生成，所以这里不看退出码，只看有没有新版本目录。
        var created = GetVersionIds(versionsRoot).Where(id => !before.Contains(id)).ToList();
        var versionId = created.FirstOrDefault(id =>
            File.Exists(Path.Combine(versionsRoot, id, id + ".json")));

        // 重装场景：目录上次已经生成过（例如上次安装中途失败），安装器不会新建目录，
        // 这时回退去找"确实是 OptiFine 生成"的版本目录，取最近写入的那个，让重装也能走完。
        versionId ??= FindOptiFineVersion(versionsRoot);
        if (versionId is null)
        {
            return new OptiFineInstallOutcome(false, null,
                "OptiFine 安装器没有生成版本目录。" + Summarize(output));
        }

        progress?.Report(new InstallProgress("正在补齐 launchwrapper", 80));
        var missing = await EnsureLibrariesAsync(gameRoot, versionId, cancellationToken).ConfigureAwait(false);

        // StarLight 处理 inheritsFrom 时会把父版本与子版本的 minecraftArguments 直接拼起来，
        // 而 OptiFine 生成的 json 自带一套完整参数（还带 tweakClass），拼完会出现两个 --gameDir
        // 直接报 MultipleArgumentsForOptionException。这里把它改写成不依赖父版本的完整 json。
        var standalone = await BuildStandaloneJsonAsync(gameRoot, versionId, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress("OptiFine 安装完成", 100));
        return new OptiFineInstallOutcome(true, versionId,
            $"OptiFine 安装完成：{versionId}"
            + (standalone ? "（已生成可直接启动的版本配置）" : string.Empty)
            + (missing > 0 ? $"（已补下 {missing} 个依赖库）" : string.Empty));
    }

    /// <summary>
    /// 把 OptiFine 生成的「inheritsFrom 原版」版本 json 改写成独立版本：
    /// 以原版 json 为底 + 追加 OptiFine 新增的库 + 把 tweakClass 接到原版参数后面 + 去掉 inheritsFrom。
    /// </summary>
    private static async Task<bool> BuildStandaloneJsonAsync(
        string gameRoot,
        string versionId,
        CancellationToken cancellationToken)
    {
        var versionJsonPath = Path.Combine(gameRoot, "versions", versionId, versionId + ".json");
        if (!File.Exists(versionJsonPath))
        {
            return false;
        }

        using var optifineDocument = JsonDocument.Parse(
            await NetCompat.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false));
        var optifine = optifineDocument.RootElement;
        if (!optifine.TryGetProperty("inheritsFrom", out var inherits) ||
            inherits.GetString() is not { Length: > 0 } vanillaId)
        {
            return false; // 已经是独立版本
        }

        var vanillaJsonPath = Path.Combine(gameRoot, "versions", vanillaId, vanillaId + ".json");
        if (!File.Exists(vanillaJsonPath))
        {
            return false;
        }

        using var vanillaDocument = JsonDocument.Parse(
            await NetCompat.ReadAllTextAsync(vanillaJsonPath, cancellationToken).ConfigureAwait(false));
        var vanilla = vanillaDocument.RootElement;

        // 合并库：原版的全部 + OptiFine 新增的，**只丢掉内容完全一样的重复项**。
        //
        // 注意不能按 name 去重：1.16.x / 1.17.x 那一代的官方版本 JSON 里，同一个 LWJGL 库会出现
        // 两条同名条目 —— 一条只有 artifact，另一条带 classifiers + natives。按 name 去重会把带
        // natives 的那条吃掉，独立版本 JSON 里就再也没有 natives 可解压，启动直接报
        // UnsatisfiedLinkError: Failed to locate library: lwjgl.dll（2026-10-06 1.16.4 光影实测）。
        var libraries = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (vanilla.TryGetProperty("libraries", out var vanillaLibraries))
        {
            foreach (var library in vanillaLibraries.EnumerateArray())
            {
                if (seen.Add(library.GetRawText()))
                {
                    libraries.Add(library.GetRawText());
                }
            }
        }

        if (optifine.TryGetProperty("libraries", out var optifineLibraries))
        {
            foreach (var library in optifineLibraries.EnumerateArray())
            {
                if (seen.Add(library.GetRawText()))
                {
                    libraries.Add(library.GetRawText());
                }
            }
        }

        // 参数：原版的 + OptiFine 多出来的部分（一般是 --tweakClass optifine.OptiFineTweaker）
        var vanillaArguments = vanilla.TryGetProperty("minecraftArguments", out var va) ? va.GetString() ?? string.Empty : string.Empty;
        var optifineArguments = optifine.TryGetProperty("minecraftArguments", out var oa) ? oa.GetString() ?? string.Empty : string.Empty;
        var extraArguments = optifineArguments.StartsWith(vanillaArguments, StringComparison.Ordinal)
            ? optifineArguments[vanillaArguments.Length..].Trim()
            : string.Empty;

        var mainClass = optifine.TryGetProperty("mainClass", out var mc) && mc.GetString() is { Length: > 0 } main
            ? main
            : vanilla.TryGetProperty("mainClass", out var vm) ? vm.GetString() : null;

        var builder = new System.Text.StringBuilder();
        builder.Append("{\n");
        builder.Append($"  \"id\": {JsonSerializer.Serialize(versionId)},\n");
        if (mainClass is not null)
        {
            builder.Append($"  \"mainClass\": {JsonSerializer.Serialize(mainClass)},\n");
        }

        // 1.13+ 的版本 JSON 用 arguments.game / arguments.jvm 数组；更早的版本用 minecraftArguments 字符串。
        // 之前只处理了后者，导致 1.13+ 的 OptiFine 版本没有任何启动参数（tweakClass 丢失 → 启动即崩）。
        if (vanilla.TryGetProperty("arguments", out var vanillaArgs))
        {
            builder.Append("  \"arguments\": {\n");
            builder.Append($"    \"game\": [{string.Join(", ", MergeArguments(vanillaArgs, optifine, "game"))}],\n");
            builder.Append($"    \"jvm\": [{string.Join(", ", MergeArguments(vanillaArgs, optifine, "jvm"))}]\n");
            builder.Append("  },\n");
        }
        else
        {
            builder.Append($"  \"minecraftArguments\": {JsonSerializer.Serialize((vanillaArguments + " " + extraArguments).Trim())},\n");
        }
        foreach (var property in new[] { "type", "assets", "assetIndex", "downloads", "javaVersion", "logging", "complianceLevel", "minimumLauncherVersion" })
        {
            if (vanilla.TryGetProperty(property, out var value))
            {
                builder.Append($"  {JsonSerializer.Serialize(property)}: {value.GetRawText()},\n");
            }
        }

        builder.Append("  \"libraries\": [\n");
        builder.Append(string.Join(",\n", libraries.Select(l => "    " + l.Replace("\n", "\n    "))));
        builder.Append("\n  ]\n}\n");

        await NetCompat.WriteAllTextAsync(versionJsonPath, builder.ToString(), new System.Text.UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 在 versions 目录里找"OptiFine 生成的版本"：版本 JSON 的 libraries 里带 <c>optifine:OptiFine:</c>。
    /// 重装时安装器通常不会新建目录，靠它兜底；取最近写入的那个，避免选中很早以前的其它光影版本。
    /// </summary>
    private static string? FindOptiFineVersion(string versionsRoot)
    {
        var candidates = new List<(string Id, DateTime Written)>();
        foreach (var id in GetVersionIds(versionsRoot))
        {
            var jsonPath = Path.Combine(versionsRoot, id, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (document.RootElement.TryGetProperty("libraries", out var libraries) &&
                    libraries.ValueKind == JsonValueKind.Array &&
                    libraries.EnumerateArray().Any(l =>
                        l.TryGetProperty("name", out var name) &&
                        (name.GetString() ?? string.Empty).StartsWith("optifine:OptiFine:", StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add((id, File.GetLastWriteTime(jsonPath)));
                }
            }
            catch (Exception)
            {
                // 读不了的目录直接跳过
            }
        }

        return candidates.OrderByDescending(c => c.Written).Select(c => c.Id).FirstOrDefault();
    }

    private static string Summarize(List<string> lines)
    {
        lock (lines)
        {
            var tail = lines.TakeLast(5).ToList();
            return tail.Count == 0 ? string.Empty : "（安装器输出：" + string.Join(" / ", tail) + "）";
        }
    }

    private static HashSet<string> GetVersionIds(string versionsRoot) =>
        Directory.Exists(versionsRoot)
            ? Directory.GetDirectories(versionsRoot).Select(Path.GetFileName).Where(n => n is not null)
                .Select(n => n!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    /// <summary>把内嵌的转发类写到磁盘（OptiFine 安装器需要一个真实存在的 class 文件）。</summary>
    private static async Task ExtractHelperAsync(string path, CancellationToken cancellationToken)
    {
        using var resource = typeof(OptiFineInstallerService).Assembly
            .GetManifestResourceStream(HelperResource)
            ?? throw new InvalidOperationException($"未找到内嵌资源：{HelperResource}");
        using var file = File.Create(path);
        await resource.CopyToAsync(file, 81920, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 版本 JSON 里引用的库（OptiFine 自己已放好，只有 launchwrapper 这类官方库需要补下载）。
    /// 返回补下来的库数量。
    /// </summary>
    private static async Task<int> EnsureLibrariesAsync(string gameRoot, string versionId, CancellationToken cancellationToken)
    {
        var jsonPath = Path.Combine(gameRoot, "versions", versionId, versionId + ".json");
        using var document = JsonDocument.Parse(await NetCompat.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("libraries", out var libraries) ||
            libraries.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.1");

        var downloaded = 0;
        foreach (var library in libraries.EnumerateArray())
        {
            var name = library.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var coordinates = ParseCoordinates(name!);
            if (coordinates is not { } maven)
            {
                continue;
            }

            var relative = $"{maven.Group.Replace('.', '/')}/{maven.Artifact}/{maven.Version}/{maven.Artifact}-{maven.Version}.jar";
            var target = Path.Combine(gameRoot, "libraries", relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            foreach (var mirror in LibraryMirrors)
            {
                try
                {
                    var bytes = await http.GetByteArrayAsync(mirror + relative, cancellationToken).ConfigureAwait(false);
                    await NetCompat.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
                    downloaded++;
                    break;
                }
                catch (Exception)
                {
                    // 换下一个源
                }
            }
        }

        return downloaded;
    }

    private static (string Group, string Artifact, string Version)? ParseCoordinates(string name)
    {
        var parts = name.Split(':');
        return parts.Length >= 3 ? (parts[0], parts[1], parts[2]) : null;
    }

/// <summary>
    /// 合并 vanilla 与 OptiFine 的 arguments 数组（game / jvm），OptiFine 新增的条目追加在后面。
    ///
    /// 1.13+ 的版本 JSON 里 <c>arguments.game/jvm</c> 的元素**不一定是字符串**：Mojang 用带
    /// <c>rules</c> 的对象表达"按条件生效的参数"（如 <c>--demo</c>、<c>-Dos.name=Windows 10</c>）。
    /// 所以这里保留每个元素的原始 JSON 文本，不能再当成字符串读——旧实现直接 <c>GetString()</c>
    /// 会抛 <c>InvalidOperationException</c>，导致 1.13+ 的 OptiFine 独立版本 JSON 根本生成不出来。
    /// </summary>
    internal static List<string> MergeArguments(JsonElement vanillaArguments, JsonElement optifineRoot, string section)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(JsonElement element)
        {
            var raw = element.GetRawText();
            if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array || seen.Add(raw))
            {
                result.Add(raw);
            }
        }

        if (vanillaArguments.TryGetProperty(section, out var vanilla) && vanilla.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in vanilla.EnumerateArray())
            {
                Add(element);
            }
        }

        if (optifineRoot.TryGetProperty("arguments", out var optifineArguments) &&
            optifineArguments.TryGetProperty(section, out var extra) &&
            extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in extra.EnumerateArray())
            {
                Add(element);
            }
        }

        return result;
    }
}
