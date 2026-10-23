namespace McServerList.Core.Launcher;

/// <summary>
/// 下载"卡死"看门狗。
///
/// StarLight 内部每次请求都新建 HttpClient，**没有超时**：实测遇到过下载资源时
/// 3 条 HTTPS 连接建立后 18 分钟毫无动静（文件数、字节数都不再增长），
/// 安装既不前进也不报错，界面就永远停在某个百分比上。
///
/// 这里每 5 秒采样一次游戏目录（versions / libraries / assets\objects）的文件数与总字节数，
/// 连续 <see cref="StallTimeout"/> 没有变化就判定为卡死，取消本次尝试，
/// 由 <see cref="GameInstallationService.InstallWithFallbackAsync"/> 换源重试。
/// </summary>
internal sealed class DownloadStallGuard : IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(30);

    private readonly string _gameRoot;
    private readonly string[] _extraRoots;
    private readonly CancellationTokenSource _attempt;
    private readonly Action<string>? _onLog;
    private readonly Timer _timer;
    private readonly DateTime _startedAt = DateTime.UtcNow;

    private (int Files, long Bytes)? _lastSignature;
    private DateTime _lastChangeAt = DateTime.UtcNow;
    private int _probing;
    private bool _disposed;

    /// <param name="gameRoot">游戏根目录（versions / libraries / assets\objects 会一并采样）。</param>
    /// <param name="attempt">卡死时要取消的令牌（本次尝试的）。</param>
    /// <param name="onLog">进度/日志回调。</param>
    /// <param name="extraRoots">
    /// 额外采样的目录。**装 Forge/NeoForge 时安装包是下到 `%APPDATA%\McServerList\forge-installers` 的**，
    /// 那段时间游戏目录一个文件都不动，只盯游戏目录会把"正在慢慢下安装包"误判成卡死（实测 NeoForge 就这样被掐了三次）。
    /// </param>
    public DownloadStallGuard(
        string gameRoot,
        CancellationTokenSource attempt,
        Action<string>? onLog = null,
        IEnumerable<string>? extraRoots = null)
    {
        _gameRoot = gameRoot;
        _attempt = attempt;
        _onLog = onLog;
        _extraRoots = extraRoots?.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        _timer = new Timer(Probe, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start() => _timer.Change(ProbeInterval, ProbeInterval);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private void Probe(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _probing, 1) == 1)
        {
            return;
        }

        try
        {
            var signature = Sample();
            if (_lastSignature != signature)
            {
                _lastSignature = signature;
                _lastChangeAt = DateTime.UtcNow;
                return;
            }

            // 刚开始安装的前 30 秒不判定（版本索引/清单还没落地）
            if (DateTime.UtcNow - _startedAt < GracePeriod)
            {
                return;
            }

            if (DateTime.UtcNow - _lastChangeAt >= StallTimeout && !_attempt.IsCancellationRequested)
            {
                _onLog?.Invoke($"下载已 {StallTimeout.TotalSeconds:0} 秒没有进展（{signature.Files} 个文件 / {signature.Bytes / 1024 / 1024} MB），判定为卡死，中断本次尝试并换源重试。");
                _attempt.Cancel();
            }
        }
        catch (Exception)
        {
            // 采样失败不影响安装
        }
        finally
        {
            Interlocked.Exchange(ref _probing, 0);
        }
    }

    private (int Files, long Bytes) Sample()
    {
        var files = 0;
        long bytes = 0;
        foreach (var relative in new[] { "versions", "libraries", Path.Combine("assets", "objects") })
        {
            var directory = Path.Combine(_gameRoot, relative);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
                catch (IOException)
                {
                    // 文件正在写，忽略
                }
            }
        }

        foreach (var extra in _extraRoots)
        {
            foreach (var path in Directory.EnumerateFiles(extra, "*", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    files++;
                }
                catch (IOException)
                {
                    // 文件正在写，忽略
                }
            }
        }

        return (files, bytes);
    }
}
