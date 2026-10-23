using System.Text;
using System.Windows;
using McServerList.App.Services;
using McServerList.Core.Launcher;

namespace McServerList.App.Views;

/// <summary>
/// Java 检测窗口：打开先做**快扫**（注册表 / JAVA_HOME / PATH / 常见安装目录 / 上次深度扫描的记忆），
/// 结果不够时再点「深度扫描」把各磁盘翻一遍（默认 30 秒预算，可随时停止）。
/// 扫描过程实时显示，关闭窗口会把结果写回会话缓存。
/// </summary>
public partial class JavaSearchWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private readonly StringBuilder _log = new();
    private IReadOnlyList<JavaRuntime> _result = [];
    private CancellationTokenSource? _deepScan;
    private bool _busy;
    private bool _closed;

    public JavaSearchWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await FastScanAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _deepScan?.Cancel();
        };
    }

    /// <summary>本次检测到的 Java 列表（供调用方刷新界面）。</summary>
    public IReadOnlyList<JavaRuntime> Result => _result;

    /// <summary>快扫：注册表 / 环境变量 / 常见目录 / 官方启动器运行时，通常不到 1 秒。</summary>
    private async Task FastScanAsync()
    {
        SearchTitle.Text = "正在快速扫描…";
        AppendLog("① 快速扫描：注册表 / JAVA_HOME / PATH / 常见安装目录 / 官方启动器运行时 / 上次深度扫描的记忆…");
        try
        {
            var found = await Task.Run(() => _session.Java.Detect(
                deepSearch: false,
                onProgress: message => Dispatcher.Invoke(() => AppendLog(message))));
            if (_closed)
            {
                return;
            }

            ApplyResults(found, "快速扫描");
        }
        catch (Exception ex)
        {
            AppendLog("快速扫描失败：" + ex.Message);
        }

        if (_closed)
        {
            return;
        }

        AppendLog("需要更多 Java 的话，点右下角「深度扫描」把各磁盘也翻一遍（最多 30 秒，可随时停止）。");
        SearchTitle.Text = "快速扫描完成";
        SearchProgress.IsIndeterminate = false;
        SearchProgress.Value = 100;
        DoneButton.IsEnabled = true;
        DeepScanButton.IsEnabled = true;
        DeepScanButton.Focus();
    }

    /// <summary>深度扫描：遍历用户目录与各磁盘，找不到就停在自己的时间预算上；再点一次按钮即中断。</summary>
    private async void OnDeepScan(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            // 第二下 = 停止扫描
            DeepScanButton.Content = "正在停止…";
            DeepScanButton.IsEnabled = false;
            _deepScan?.Cancel();
            return;
        }

        _busy = true;
        _deepScan = new CancellationTokenSource();
        var token = _deepScan.Token;
        DeepScanButton.Content = "停止扫描";
        SearchTitle.Text = "正在深度扫描…";
        SearchProgress.IsIndeterminate = true;
        AppendLog($"② 深度扫描：用户目录 + 各磁盘，最多 {_session.Java.DeepScanBudget.TotalSeconds:0} 秒（可随时停止）…");

        try
        {
            var found = await Task.Run(
                () => _session.Java.Detect(
                    deepSearch: true,
                    onProgress: message => Dispatcher.Invoke(() => AppendLog(message)),
                    cancellationToken: token),
                token);
            if (!_closed)
            {
                ApplyResults(found, "深度扫描");
                SearchTitle.Text = "已完成深度扫描";
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("深度扫描已停止（已经找到的 Java 已保留）。");
            SearchTitle.Text = "深度扫描已停止";
        }
        catch (Exception ex)
        {
            AppendLog("深度扫描失败：" + ex.Message);
            SearchTitle.Text = "深度扫描失败";
        }
        finally
        {
            _deepScan?.Dispose();
            _deepScan = null;
            _busy = false;
            SearchProgress.IsIndeterminate = false;
            SearchProgress.Value = 100;
            DeepScanButton.Content = $"深度扫描（{_session.Java.DeepScanBudget.TotalSeconds:0} 秒）";
            DeepScanButton.IsEnabled = true;
            DoneButton.IsEnabled = true;
        }
    }

    /// <summary>把新结果并进列表（按路径去重），并把合并后的完整列表写回会话缓存。</summary>
    private void ApplyResults(IReadOnlyList<JavaRuntime> found, string phase)
    {
        var merged = _result.ToList();
        var added = new List<JavaRuntime>();
        foreach (var java in found)
        {
            if (merged.Any(m => string.Equals(m.JavaPath, java.JavaPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(java);
            added.Add(java);
        }

        _result = merged
            .OrderByDescending(j => j.MajorVersion)
            .ThenBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _session.SetJavaCache(_result); // 关闭窗口后不再重扫

        foreach (var java in added)
        {
            AppendLog($"  · {java.DisplayName}  {java.JavaPath}");
        }

        AppendLog($"{phase}结束：本次新增 {added.Count} 个，合计 {_result.Count} 个 Java 运行时。");
        SummaryText.Text = $"共 {_result.Count} 个";
    }

    private void AppendLog(string message)
    {
        _log.AppendLine(message);
        LogText.Text = _log.ToString();
        LogScroll.ScrollToEnd(); // 始终滚动到最新一行
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        _deepScan?.Cancel();
        Close();
    }

    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
