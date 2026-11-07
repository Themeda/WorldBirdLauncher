using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WorldBirdLauncher.App.Services;

namespace WorldBirdLauncher.App.Views;

/// <summary>资源下载页：游戏本体 / 加载器 / 光影 / 模组的下载入口。</summary>
public partial class DownloadPage : UserControl
{
    private readonly AppSession _session = AppSession.Current;
    private bool _suppressConcurrency;

    public DownloadPage()
    {
        InitializeComponent();
        DataContext = DownloadManager.Current;
        Loaded += (_, _) => ReloadConcurrency();
    }

    private void ReloadConcurrency()
    {
        _suppressConcurrency = true;
        var value = DownloadManager.Current.MaxConcurrency;
        ConcurrencyBox.SelectedIndex = NetCompat.Clamp(value, 1, 4) - 1;
        _suppressConcurrency = false;
    }

    private void OnConcurrencyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConcurrency || ConcurrencyBox.SelectedIndex < 0)
        {
            return;
        }

        _session.Settings.MaxConcurrentDownloads = ConcurrencyBox.SelectedIndex + 1;
        _session.Settings.Save();
        StatusText.Text = $"同时下载任务数已设为 {_session.Settings.MaxConcurrentDownloads}。";
        DownloadManager.Current.PumpIfIdle();
    }

    private void OnCancelTask(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DownloadTask task)
        {
            DownloadManager.Current.Cancel(task);
        }
    }

    private void OnClearFinished(object sender, RoutedEventArgs e) => DownloadManager.Current.ClearFinished();

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(AppSettings.Directory, "launcher-error.log");
            if (!File.Exists(logPath))
            {
                File.WriteAllText(logPath, "（暂无日志）");
            }

            Process.Start(new ProcessStartInfo(logPath) { UseShellExecute = true });
            StatusText.Text = $"已打开日志：{logPath}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开日志失败：{ex.Message}";
        }
    }

    private void OnDownloadMods(object sender, RoutedEventArgs e)
    {
        new ModDownloadWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        StatusText.Text = "下载任务已在后台执行，可在下方任务列表查看进度。";
    }

    private void OnOpenGameFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = _session.GameRoot;
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo(root) { UseShellExecute = true });
            StatusText.Text = $"已打开：{root}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开失败：{ex.Message}";
        }
    }
}
