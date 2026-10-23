using System.Windows;
using McServerList.App.Services;
using McServerList.Core.Launcher;

namespace McServerList.App.Views;

/// <summary>
/// 「下载适配 Java」独立窗口：先用**已检测到的** Java 列表判断；
/// 没有适配版本时先做一次**深度扫描**（默认 30 秒，关窗口即取消），
/// 确实没有才让用户下载（避免为了一个藏在 D:\java 里的 Java 白下 200MB）。
/// </summary>
public partial class JavaDownloadWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private readonly int _wantedVersion;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    public JavaDownloadWindow(int featureVersion, string versionId)
    {
        InitializeComponent();
        // 1.13 之前的老版本只能用 Java 8（Java 9+ 会崩），所以下载目标也跟着换成 8
        _wantedVersion = JavaRuntimeService.AllowsNewerJava(versionId) ? featureVersion : 8;
        VersionId = versionId;
        Closed += (_, _) => _lifetime.Cancel();

        RequirementText.Text = _wantedVersion == featureVersion
            ? $"{versionId} 需要 Java {featureVersion}"
            : $"{versionId} 属于老版本（1.13 之前），只能用 Java {_wantedVersion}";
        var detected = _session.GetJavaRuntimes().ToList();
        var existing = JavaRuntimeService.PickBest(detected, versionId, _wantedVersion);
        if (existing is not null)
        {
            UseExisting(existing, detected.Count);
        }
        else
        {
            StatusMessage = "正在确认…";
            StatusText.Text = "正在深度扫描本机 Java（最多 30 秒，可关闭窗口取消）…";
            DetailText.Text = $"快扫只找到 {detected.Count} 个 Java，都不满足 Java {_wantedVersion}；" +
                              "先翻一遍各磁盘再决定要不要下载。";
            DownloadProgress.Visibility = Visibility.Visible;
            DownloadProgress.IsIndeterminate = true;
            StartButton.IsEnabled = false;
            Loaded += async (_, _) => await DeepScanAsync();
        }
    }

    public string VersionId { get; }

    /// <summary>最终可用的 Java（已存在或刚下载完成）。</summary>
    public JavaRuntime? InstalledJava { get; private set; }

    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>已有满足要求的 Java：直接把它交给调用方，不给下载按钮。</summary>
    private void UseExisting(JavaRuntime existing, int detectedCount)
    {
        InstalledJava = existing;
        StatusMessage = $"已存在适配版本（{existing.DisplayName}），无需下载。";
        StatusText.Text = StatusMessage;
        DetailText.Text = $"本机已检测到 {detectedCount} 个 Java 运行时，其中 {existing.DisplayName} 满足要求：\n{existing.JavaPath}";
        StartButton.Visibility = Visibility.Collapsed;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Visibility = Visibility.Collapsed;
    }

    /// <summary>快扫没找到适配版本时，先深扫一遍再决定是否真的需要下载。</summary>
    private async Task DeepScanAsync()
    {
        try
        {
            var merged = await Task.Run(
                () => _session.DeepScanJava(
                    onProgress: message => Dispatcher.Invoke(() => StatusText.Text = message),
                    cancellationToken: _lifetime.Token),
                _lifetime.Token);

            var existing = JavaRuntimeService.PickBest(merged, VersionId, _wantedVersion);
            if (existing is not null)
            {
                UseExisting(existing, merged.Count);
                return;
            }

            StatusMessage = "尚未下载。";
            StatusText.Text = StatusMessage;
            DetailText.Text = $"深度扫描后本机共 {merged.Count} 个 Java 运行时，但都不满足 Java {_wantedVersion}。\n" +
                              $"点「开始下载」将从 Adoptium 下载 JDK {_wantedVersion}（约 190–200 MB），" +
                              "安装到 %APPDATA%\\McServerList\\runtime。";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // 关窗口取消：不用提示
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败：{ex.Message}";
            StatusText.Text = StatusMessage + "；可以直接点「开始下载」。";
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
            StartButton.IsEnabled = true;
        }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        StartButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Value = 0;

        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.Percentage >= 0)
                {
                    DownloadProgress.Value = p.Percentage;
                }

                StatusText.Text = p.Detail is null ? $"{p.Stage}（{p.Percentage}%）" : $"{p.Stage} · {p.Detail}";
            });

            var java = await _session.Java.InstallAsync(_wantedVersion, progress);
            InstalledJava = java;
            _session.SetJavaCache(_session.Java.Detect(deepSearch: false).Concat([java]).ToList());
            StatusMessage = $"Java 安装完成：{java.DisplayName}";
            StatusText.Text = StatusMessage;
            DetailText.Text = "安装完成，可以直接关闭本窗口；启动器已自动选中该 Java。";
            StartButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusMessage = $"下载失败：{ex.Message}";
            StatusText.Text = StatusMessage + "（可稍后重试，或手动安装 Adoptium JDK）";
            App.LogLine($"Java {_wantedVersion} 下载失败：{ex}");
            StartButton.IsEnabled = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
