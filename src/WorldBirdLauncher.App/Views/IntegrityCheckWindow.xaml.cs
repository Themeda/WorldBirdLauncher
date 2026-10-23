using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Views;

/// <summary>「检查完整性」独立窗口：可选择任意已安装版本做必要文件校验。</summary>
public partial class IntegrityCheckWindow : Window
{
    private const int MaxLogLines = 2000;

    private readonly AppSession _session = AppSession.Current;
    private readonly IntegrityService _integrity = new();
    private readonly Queue<IntegrityLogEntry> _pending = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _followTail = true;
    private int _lineCount;
    private string? _lastResultText;

    public IntegrityCheckWindow(string? preferredVersionId)
    {
        InitializeComponent();
        _flushTimer.Tick += (_, _) => FlushLog();
        Loaded += (_, _) =>
        {
            var installed = _session.Installation.GetInstalledVersions(_session.GameRoot).Where(v => v.IsUsable).Select(v => v.Id).ToList();
            VersionBox.ItemsSource = installed;
            var index = preferredVersionId is null ? -1 : installed.IndexOf(preferredVersionId);
            VersionBox.SelectedIndex = index >= 0 ? index : installed.Count > 0 ? 0 : -1;
            AppendLog(new IntegrityLogEntry(
                IntegrityLogKind.Info, "选择版本后点「开始检查」，将按版本 JSON 里的 SHA-1 校验客户端 jar、库文件与资源文件。"));
            FlushLog();
        };
    }

    private async void OnCheck(object sender, RoutedEventArgs e)
    {
        if (_busy || VersionBox.SelectedItem is not string versionId)
        {
            AppendLog(new IntegrityLogEntry(IntegrityLogKind.Fail, "请先选择一个已安装的版本。"));
            FlushLog();
            return;
        }

        _busy = true;
        _cancellation = new CancellationTokenSource();
        _lastResultText = null;
        CopyResultButton.IsEnabled = false;
        CopyResultButton.Content = "复制结果";
        SetRunningState(true);
        StatusText.Text = $"正在检查 {versionId} …";
        AppendLog(new IntegrityLogEntry(IntegrityLogKind.Header, $"开始检查 {versionId}"));

        try
        {
            var progress = new Progress<IntegrityLogEntry>(AppendLog);
            var report = await _integrity.CheckAsync(_session.GameRoot, versionId, progress, _cancellation.Token);

            AppendLog(new IntegrityLogEntry(IntegrityLogKind.Header, "检查结果"));
            AppendLog(new IntegrityLogEntry(
                report.IsHealthy ? IntegrityLogKind.Pass : IntegrityLogKind.Fail,
                report.IsHealthy ? $"✓ {versionId} 完整性检查通过" : $"✗ {versionId} 存在问题"));
            AppendLog(new IntegrityLogEntry(IntegrityLogKind.Info, report.Summary));
            _lastResultText = BuildResultText(versionId, report);
            CopyResultButton.IsEnabled = true;
            foreach (var issue in report.Issues.Take(80))
            {
                AppendLog(new IntegrityLogEntry(IntegrityLogKind.Fail, $"[{issue.Category}] {issue.Target}：{issue.Reason}"));
            }

            if (report.Issues.Count > 80)
            {
                AppendLog(new IntegrityLogEntry(IntegrityLogKind.Info, $"（其余 {report.Issues.Count - 80} 条已省略）"));
            }

            if (report.Issues.Count > 0)
            {
                AppendLog(new IntegrityLogEntry(IntegrityLogKind.Info, "可在设置页点「安装版本…」重装该版本补全文件。"));
            }

            StatusText.Text = report.IsHealthy ? "检查完成：全部通过" : $"检查完成：{report.Missing} 个缺失、{report.HashMismatch} 个哈希不符";
        }
        catch (OperationCanceledException)
        {
            AppendLog(new IntegrityLogEntry(IntegrityLogKind.Info, "已终止校验（已校验的部分结果在上面）。"));
            StatusText.Text = "已终止";
        }
        catch (Exception ex)
        {
            AppendLog(new IntegrityLogEntry(IntegrityLogKind.Fail, $"检查失败：{ex.Message}"));
            StatusText.Text = "检查失败";
            App.LogLine($"完整性检查失败（{versionId}）：{ex}");
        }
        finally
        {
            _busy = false;
            _cancellation?.Dispose();
            _cancellation = null;
            SetRunningState(false);
            _flushTimer.Stop();
            FlushLog(); // 收尾把剩余日志一次性刷出来
        }
    }

    private void SetRunningState(bool running)
    {
        VersionBox.IsEnabled = !running;
        CheckButton.IsEnabled = !running;
        CheckButton.Content = running ? "检查中…" : "开始检查";
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = running;
        if (running)
        {
            _flushTimer.Start();
        }
    }

    /// <summary>「复制结果」复制的内容：版本、时间、汇总与全部问题明细。</summary>
    private static string BuildResultText(string versionId, IntegrityReport report)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"Minecraft 完整性检查结果 —— {versionId} —— {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine(report.IsHealthy
            ? "结论：通过（客户端 jar / 库 / 资源 均已通过 SHA-1 校验）"
            : "结论：存在问题");
        builder.AppendLine(report.Summary);
        if (report.Issues.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("问题明细：");
            foreach (var issue in report.Issues)
            {
                builder.AppendLine($"[{issue.Category}] {issue.Target}：{issue.Reason}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private void OnCopyResult(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastResultText))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_lastResultText);
            CopyResultButton.Content = "已复制";
        }
        catch (Exception ex)
        {
            App.LogLine($"复制完整性结果失败：{ex.Message}");
            CopyResultButton.Content = "复制失败";
        }

        _ = RestoreCopyButtonAsync();
    }

    private async Task RestoreCopyButtonAsync()
    {
        await Task.Delay(1500);
        if (!_busy)
        {
            CopyResultButton.Content = "复制结果";
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        StatusText.Text = "正在终止…";
        _cancellation?.Cancel();
    }

    private void AppendLog(IntegrityLogEntry entry)
    {
        _pending.Enqueue(entry);
        if (!_flushTimer.IsEnabled)
        {
            FlushLog(); // 非检查阶段（如窗口刚打开）直接刷
        }
    }

    private void FlushLog()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        while (_pending.Count > 0)
        {
            var entry = _pending.Dequeue();
            var prefix = entry.Kind switch
            {
                IntegrityLogKind.Pass => "✓ ",
                IntegrityLogKind.Fail => "✗ ",
                _ => string.Empty,
            };
            LogText.Inlines.Add(new Run(prefix + entry.Text + Environment.NewLine)
            {
                Foreground = entry.Kind switch
                {
                    IntegrityLogKind.Fail => new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B)),
                    IntegrityLogKind.Pass => new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)),
                    IntegrityLogKind.Header => new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)),
                    _ => (Brush)FindResource("TextMutedBrush"),
                },
            });
            _lineCount++;
        }

        // 只保留最近 MaxLogLines 行，防止几千个资源文件把内存撑爆
        while (_lineCount > MaxLogLines && LogText.Inlines.Count > 0)
        {
            LogText.Inlines.Remove(LogText.Inlines.FirstInline);
            _lineCount--;
        }

        if (_followTail)
        {
            LogScroll.ScrollToEnd();
        }
    }

    /// <summary>用户手动往上滚看历史时暂停自动跟随，滚回底部自动恢复。</summary>
    private void OnLogScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 && e.VerticalChange == 0)
        {
            return; // 只是内容变高，不算用户滚动
        }

        _followTail = LogScroll.VerticalOffset >= LogScroll.ScrollableHeight - 4;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            var confirmed = MessageWindow.Confirm(this, "检查完整性",
                "校验还在进行中，确定要终止并关闭窗口吗？", "终止并关闭", "继续校验");
            if (!confirmed)
            {
                return;
            }

            _cancellation?.Cancel();
        }

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
