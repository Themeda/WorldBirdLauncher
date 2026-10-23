using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>
/// 安装进度看门狗。
///
/// StarLight 只会报"阶段百分比"（0/10/20/30/50/60/80/90/100），其中「下载游戏资源」这一段要下几千个
/// 资源文件、会长时间停在 60% 不动，界面看起来就像卡死。这里改成按**真实文件**统计：
/// 从版本 JSON 与资源索引算出"这个版本应该有哪些文件"，每 500ms 数一遍已就绪的数量，
/// 就能给出连续更新的百分比、当前正在写入的文件以及该文件的"已下/总大小"。
/// </summary>
public sealed class InstallProgressWatcher : IDisposable
{
    private const int DefaultIntervalMs = 500;

    private sealed record ExpectedFile(string Path, long Size, string Category);

    private readonly string _gameRoot;
    private readonly IProgress<InstallProgress>? _progress;
    private readonly int _intervalMs;
    private readonly System.Threading.Timer _timer;
    private readonly Dictionary<string, DateTime> _seenJsons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExpectedFile> _expected = [];
    private readonly object _gate = new();

    private string _phase = "正在准备安装";
    private int _emitting;
    private bool _disposed;
    private bool _opaqueMode;
    private string? _lastFile;
    private long _lastFileDone;
    private long _lastFileTotal;
    private DateTime _externalUntil = DateTime.MinValue;

    public InstallProgressWatcher(string gameRoot, IProgress<InstallProgress>? progress, int intervalMs = DefaultIntervalMs)
    {
        _gameRoot = gameRoot;
        _progress = progress;
        _intervalMs = intervalMs <= 0 ? DefaultIntervalMs : intervalMs;
        _timer = new System.Threading.Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>已经开始真正的工作（用于把"排队/准备"阶段也算进去）。</summary>
    public void Start(string initialPhase = "正在准备安装")
    {
        lock (_gate)
        {
            _phase = initialPhase;
        }

        _timer.Change(0, _intervalMs);
    }

    /// <summary>
    /// 接收外部进度报告：
    /// - 带 <see cref="InstallProgress.File"/> 的（我们自己并发下载器报的）直接用它的字节级进度，并暂停自身统计 1.5 秒；
    /// - 其它（StarLight 的阶段百分比）只取阶段文案，百分比改用文件统计。
    /// </summary>
    public void ReportExternal(InstallProgress progress)
    {
        if (progress.File is not null)
        {
            lock (_gate)
            {
                _phase = progress.Stage;
                _lastFile = progress.File;
                _lastFileDone = progress.FileDone;
                _lastFileTotal = progress.FileTotal;
                _externalUntil = DateTime.UtcNow.AddSeconds(1.5);
            }

            _progress?.Report(progress);
            return;
        }

        if (string.IsNullOrWhiteSpace(progress.Stage))
        {
            return;
        }

        lock (_gate)
        {
            _phase = progress.Detail is { Length: > 0 } detail
                ? $"{progress.Stage} · {detail}"
                : progress.Stage;
        }
    }

    /// <summary>没有文件可统计的长步骤（如 OptiFine 打补丁）：报不确定态 + 已用时。</summary>
    public void ReportOpaqueStep(string stage, TimeSpan elapsed) =>
        _progress?.Report(new InstallProgress(stage, -1, $"已用时 {elapsed:mm\\:ss}"));

    /// <summary>进入/退出"无文件可统计"的长步骤：进入后暂停文件统计，避免两类进度互相打架。</summary>
    public void SetOpaqueMode(bool enabled)
    {
        lock (_gate)
        {
            _opaqueMode = enabled;
        }
    }

    /// <summary>结束前把最后一次统计结果（100%）发出去。</summary>
    public void ReportCompleted(string stage = "安装已完成")
    {
        lock (_gate)
        {
            _phase = stage;
            _lastFile = null; // 结束时清掉"当前文件"
        }

        RefreshExpected();
        _progress?.Report(new InstallProgress(stage, 100, DescribeReady()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Tick(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _emitting, 1) == 1)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_opaqueMode)
                {
                    return; // 交给 ReportOpaqueStep 报
                }

                if (DateTime.UtcNow < _externalUntil)
                {
                    return; // 外部（并发下载器）正在提供更精确的字节进度
                }
            }

            RefreshExpected();
            var snapshot = Measure();
            if (snapshot is null)
            {
                // 还没有可统计的文件（早于版本索引/资源索引下载完成）→ 不确定态
                string phase;
                lock (_gate)
                {
                    phase = _phase;
                }

                _progress?.Report(new InstallProgress(phase, -1, "正在准备下载清单…"));
                return;
            }

            var (percentage, ready, total, currentFile, fileDone, fileTotal) = snapshot.Value;
            string stageText;
            lock (_gate)
            {
                stageText = _phase;
            }

            var detail = currentFile is null
                ? DescribeReady()
                : $"当前文件：{currentFile}（{FormatSize(fileDone)} / {(fileTotal > 0 ? FormatSize(fileTotal) : "未知")}）";
            _progress?.Report(new InstallProgress(
                $"{stageText}（{ready}/{total} 个文件）",
                percentage,
                detail,
                currentFile,
                fileDone,
                fileTotal));
        }
        catch (Exception)
        {
            // 统计失败不影响安装本身
        }
        finally
        {
            Interlocked.Exchange(ref _emitting, 0);
        }
    }

    private string DescribeReady()
    {
        lock (_gate)
        {
            return _expected.Count == 0 ? "正在准备下载清单…" : $"已就绪 {_expected.Count(f => File.Exists(f.Path))}/{_expected.Count} 个文件";
        }
    }

    /// <summary>
    /// 重新扫描"应该有哪些文件"：版本 JSON（客户端 jar + 库）+ 资源索引（每个资源对象）。
    /// 只在该 JSON 文件发生变化时解析，避免每 500ms 重读大文件。
    /// </summary>
    private void RefreshExpected()
    {
        var versionsRoot = Path.Combine(_gameRoot, "versions");
        if (Directory.Exists(versionsRoot))
        {
            foreach (var jsonPath in Directory.EnumerateFiles(versionsRoot, "*.json", SearchOption.AllDirectories))
            {
                // 版本目录里的 natives 之类的 json 不算
                if (!string.Equals(Path.GetFileNameWithoutExtension(jsonPath),
                        Path.GetFileName(Path.GetDirectoryName(jsonPath)), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddVersionJson(jsonPath);
            }
        }

        var indexesRoot = Path.Combine(_gameRoot, "assets", "indexes");
        if (Directory.Exists(indexesRoot))
        {
            foreach (var jsonPath in Directory.EnumerateFiles(indexesRoot, "*.json"))
            {
                AddAssetIndex(jsonPath);
            }
        }
    }

    private void AddVersionJson(string jsonPath)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(jsonPath);
        }
        catch (IOException)
        {
            return;
        }

        lock (_gate)
        {
            if (_seenJsons.TryGetValue(jsonPath, out var seen) && seen == stamp)
            {
                return;
            }

            _seenJsons[jsonPath] = stamp;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        }
        catch (Exception)
        {
            return; // 文件可能正在写
        }

        using (document)
        {
            var root = document.RootElement;
            var versionId = Path.GetFileNameWithoutExtension(jsonPath);
            var libraryRoot = Path.Combine(_gameRoot, "libraries");

            lock (_gate)
            {
                if (root.TryGetProperty("downloads", out var downloads) &&
                    downloads.TryGetProperty("client", out var client) &&
                    client.TryGetProperty("size", out var clientSize) &&
                    clientSize.TryGetInt64(out var clientBytes))
                {
                    Add(new ExpectedFile(Path.Combine(_gameRoot, "versions", versionId, versionId + ".jar"),
                        clientBytes, "游戏本体"));
                }

                if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var library in libraries.EnumerateArray())
                    {
                        if (!MinecraftLibraryRules.IsAllowedForThisSystem(library))
                        {
                            continue;
                        }

                        if (MinecraftLibraryRules.ResolveArtifact(library) is not { } artifact)
                        {
                            continue;
                        }

                        var path = Path.Combine(libraryRoot, artifact.Path.Replace('/', Path.DirectorySeparatorChar));
                        Add(new ExpectedFile(path, artifact.Size, "库文件"));
                    }
                }
            }
        }
    }

    private void AddAssetIndex(string jsonPath)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(jsonPath);
        }
        catch (IOException)
        {
            return;
        }

        lock (_gate)
        {
            if (_seenJsons.TryGetValue(jsonPath, out var seen) && seen == stamp)
            {
                return;
            }

            _seenJsons[jsonPath] = stamp;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        }
        catch (Exception)
        {
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var objectsRoot = Path.Combine(_gameRoot, "assets", "objects");
            lock (_gate)
            {
                foreach (var property in objects.EnumerateObject())
                {
                    var hash = property.Value.TryGetProperty("hash", out var hashElement) ? hashElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(hash) || hash!.Length < 2)
                    {
                        continue;
                    }

                    var size = property.Value.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value)
                        ? value
                        : 0;
                    Add(new ExpectedFile(Path.Combine(objectsRoot, hash[..2], hash), size, "资源"));
                }
            }
        }
    }

    private void Add(ExpectedFile file)
    {
        if (_expected.Any(e => string.Equals(e.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _expected.Add(file);
    }

    private (int Percentage, int Ready, int Total, string? CurrentFile, long FileDone, long FileTotal)? Measure()
    {
        List<ExpectedFile> expected;
        lock (_gate)
        {
            if (_expected.Count == 0)
            {
                return null;
            }

            expected = [.. _expected];
        }

        var ready = 0;
        ExpectedFile? current = null;
        var currentWriteTime = DateTime.MinValue;
        long currentLength = 0;

        foreach (var file in expected)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file.Path);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (Exception)
            {
                continue;
            }

            ready++;
            if (info.LastWriteTimeUtc > currentWriteTime)
            {
                currentWriteTime = info.LastWriteTimeUtc;
                current = file;
                currentLength = info.Length;
            }
        }

        // 只在文件"刚刚还在写"（3 秒内）时才展示为当前文件，避免结束时仍显示某个旧文件
        var isWriting = current is not null && DateTime.UtcNow - currentWriteTime < TimeSpan.FromSeconds(3);
        var percentage = (int)NetCompat.Clamp(ready * 100.0 / expected.Count, 0, 100);
        // "当前文件"要一直显示到下一个文件顶上，不能因为两次写入之间的空档就闪掉
        if (current is not null && (isWriting || _lastFile is null))
        {
            _lastFile = NetCompat.GetRelativePath(_gameRoot, current.Path);
        }

        if (_lastFile is not null && current is not null &&
            string.Equals(_lastFile, NetCompat.GetRelativePath(_gameRoot, current.Path), StringComparison.OrdinalIgnoreCase))
        {
            _lastFileDone = currentLength;
            _lastFileTotal = current.Size;
        }

        return (percentage, ready, expected.Count, _lastFile, _lastFileDone, _lastFileTotal);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
