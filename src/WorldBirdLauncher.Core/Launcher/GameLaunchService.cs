using System.Diagnostics;
using System.Globalization;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// 游戏启动。
///
/// 参数构建与 natives 解压都是**本项目自研**（<see cref="GameArgumentsBuilder"/>，行为基线是
/// StarLight.Core 2.0.5，见那边的注释），这里只负责"建参数 → 解压 natives → 拉起进程 → 转发日志"。
///
/// 历史上用过 StarLight 的 <c>MinecraftLauncher</c>，但 2.0.5 里 <c>GameCoreConfig.GameArguments</c> /
/// <c>Ip</c> / <c>Port</c> 全是"只声明不实现"（"启动后自动进服"永远不生效），所以改成自己拉起进程。
/// 进程启动参数与 StarLight 原实现保持一致（工作目录 = .minecraft 根、重定向 stdout/stderr）。
/// </summary>
public sealed class GameLaunchService
{
    /// <summary>启动游戏。返回结果中包含实际使用的参数与进程，便于排查与后续跟踪。</summary>
    public async Task<GameLaunchOutcome> LaunchAsync(
        GameLaunchRequest request,
        Action<string>? onLog = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        NetCompat.ThrowIfNull(request, nameof(request));

        if (!File.Exists(request.JavaPath))
        {
            return new GameLaunchOutcome { Success = false, Message = $"Java 不存在：{request.JavaPath}" };
        }

        if (!Directory.Exists(Path.Combine(request.GameRoot, "versions", request.VersionId)))
        {
            return new GameLaunchOutcome
            {
                Success = false,
                Message = $"游戏版本不存在或未安装：{request.VersionId}",
            };
        }

        progress?.Report(new InstallProgress("正在检查启动配置", 10));

        var extraGameArguments = BuildServerArguments(request.ServerAddress, request.VersionId);

        try
        {
            progress?.Report(new InstallProgress("正在构建启动参数", 60));
            using var builder = new GameArgumentsBuilder(request.GameRoot, request.VersionId);
            var built = await builder.BuildAsync(
                    request.Account,
                    request.MinMemoryMb,
                    request.MaxMemoryMb,
                    request.WindowWidth,
                    request.WindowHeight,
                    request.IsFullScreen,
                    request.IsVersionIsolation,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var arguments = built.Segments.ToList();

            // ★ StarLight 不会带上游戏附加参数，这里补上，实现"启动后自动进服"
            arguments.AddRange(extraGameArguments);

            foreach (var warning in built.Warnings)
            {
                SafeLog(onLog, "提示：" + warning);
            }

            // natives 解压：目标目录就是参数里 -Djava.library.path 用的那个（同一个对象算出来的，不会两边不一致）
            progress?.Report(new InstallProgress("正在解压游戏资源", 30));
            ExtractNatives(builder, built.NativesDirectory, onLog);

            var startInfo = new ProcessStartInfo
            {
                FileName = request.JavaPath,
                Arguments = string.Join(" ", arguments),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = request.GameRoot,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    SafeLog(onLog, e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    SafeLog(onLog, "[错误] " + e.Data);
                }
            };

            progress?.Report(new InstallProgress("进程启动中…", 90));
            if (!process.Start())
            {
                process.Dispose();
                return new GameLaunchOutcome { Success = false, Message = "启动器无法创建游戏进程。", Arguments = arguments };
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            progress?.Report(new InstallProgress("游戏已启动", 100));
            return new GameLaunchOutcome
            {
                Success = true,
                Message = "游戏已启动。",
                ProcessId = process.Id,
                Process = process,
                Arguments = arguments,
            };
        }
        catch (Exception ex)
        {
            return new GameLaunchOutcome { Success = false, Message = $"启动异常：{ex.Message}" };
        }
    }

    /// <summary>
    /// 把默认服务器地址转成"启动后自动进服"参数；未配置时返回空。
    ///
    /// 用 <c>--quickPlayMultiplayer 主机:端口</c>：现代 Minecraft（1.20+）已移除旧的
    /// <c>--server</c> / <c>--port</c>，实测 26.1 会打印
    /// "Completely ignored arguments: [--server, ...]"，只有 quick play 才真正进服。
    /// </summary>
    public static IReadOnlyList<string> BuildServerArguments(string? address, string? versionId = null)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return [];
        }

        var (host, port) = SplitServerAddress(address);
        if (string.IsNullOrWhiteSpace(host))
        {
            return [];
        }

        // ★ --quickPlayMultiplayer 是 1.20 才有的参数，老版本会**静默忽略**（点了启动不进服）；
        //   老版本要用 --server/--port。（现象与结论见 AGENTS.md 踩坑清单第 7 条）
        var portText = port.ToString(CultureInfo.InvariantCulture);
        return WorldBirdLauncher.Core.Models.VersionCompatibility.SupportsQuickPlay(versionId)
            ? ["--quickPlayMultiplayer", $"{host}:{portText}"]
            : ["--server", host, "--port", portText];
    }

    /// <summary>把 <c>ip:port</c> 拆成主机与端口，缺省端口为 25565。</summary>
    public static (string Host, int Port) SplitServerAddress(string? address)
    {
        var trimmed = (address ?? string.Empty).Trim();
        var separator = trimmed.LastIndexOf(':');
        if (separator > 0 && int.TryParse(trimmed[(separator + 1)..], out var port))
        {
            return (trimmed[..separator], port);
        }

        return (trimmed, 25565);
    }

    /// <summary>解压 natives，只记日志、不打断启动流程。</summary>
    private static void ExtractNatives(GameArgumentsBuilder builder, string nativesDirectory, Action<string>? onLog)
    {
        try
        {
            var extracted = builder.ExtractNatives(nativesDirectory, onLog);
            onLog?.Invoke(extracted > 0
                ? $"已解压 {extracted} 个 natives 文件 → {nativesDirectory}"
                : "提示：natives 目录为空（缺少 natives 包？），若启动报 Failed to locate library 请重新安装该版本。");
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"提示：解压 natives 失败（{ex.Message}），若启动报 Failed to locate library 请重新安装该版本。");
        }
    }

    /// <summary>
    /// natives 目标目录：从启动参数里取 <c>-Djava.library.path</c>（这是 JVM 真正会去找 dll 的地方），
    /// 取不到才按官方目录规则拼一个兜底值。
    ///
    /// 注意 StarLight 会把一堆 JVM 参数拼成**一个**巨长的参数项，所以这里不能只看 "以 … 开头"，
    /// 得在整串里找子串，并且要剥掉它自己加的双引号（实测踩过：第一次只做 StartsWith，
    /// 结果解压到了 JVM 根本不读的目录，游戏照样报 Failed to locate library: lwjgl.dll）。
    /// </summary>
    internal static string ResolveNativesDirectory(GameLaunchRequest request, IReadOnlyList<string> arguments)
    {
        const string marker = "-Djava.library.path=";
        foreach (var argument in arguments)
        {
            var index = argument.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var value = argument[(index + marker.Length)..].Trim();
            if (value.StartsWith('"'))
            {
                var end = value.IndexOf('"', 1);
                if (end > 1)
                {
                    value = value[1..end];
                }
            }
            else
            {
                var end = value.IndexOf(' ');
                if (end > 0)
                {
                    value = value[..end];
                }
            }

            // java.library.path 可能是多段（分号分隔），取第一段作为解压目标
            // net48 没有 Split(char, StringSplitOptions.TrimEntries)，用兼容层的 SplitTrim（切完逐个 Trim）
            value = NetCompat.SplitTrim(value, ';').FirstOrDefault(v => v.Length > 0) ?? string.Empty;
            if (value.Length > 0)
            {
                return value;
            }
        }

        return Path.Combine(request.GameRoot, "versions", request.VersionId, request.VersionId + "-natives");
    }

    /// <summary>
    /// 日志回调来自线程池线程，调用方若在回调里碰 UI 很可能抛异常；
    /// 这里兜一层，避免一个日志回调把整个启动器打挂（实测踩过：游戏 stdout 一到就崩）。
    /// </summary>
    private static void SafeLog(Action<string>? onLog, string line)
    {
        try
        {
            onLog?.Invoke(line);
        }
        catch (Exception)
        {
            // 记日志失败不影响游戏本体
        }
    }
}
