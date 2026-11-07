using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Launcher;
namespace WorldBirdLauncher.App.Views;

public partial class SettingsPage : UserControl
{
    private readonly AppSession _session = AppSession.Current;
    private bool _suppressPick;
    private bool _suppressTheme;
    private bool _busy;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>设置保存或版本/Java 变化后通知主窗口刷新启动页。</summary>
    public event Action? LauncherReloadRequested;

    public void Reload()
    {
        _suppressPick = true;
        GameDirBox.Text = _session.Settings.ResolveGameDirectory();
        MirrorBox.IsChecked = _session.Settings.UseMirror;
        QueryBox.IsChecked = _session.Settings.EnableQuery;
        CacheBox.Text = _session.Settings.CacheSeconds.ToString();
        MemoryBox.Text = _session.Settings.MaxMemoryMb.ToString();
        ConfigureMemorySlider();
        IsolationBox.IsChecked = _session.Settings.VersionIsolation;
        TrayAfterLaunchBox.IsChecked = _session.Settings.TrayAfterLaunch;
        TrayOnCloseBox.IsChecked = _session.Settings.TrayOnClose;
        ServerAddressBox.Text = _session.Settings.DefaultServerAddress ?? string.Empty;
        _suppressTheme = true;
        ThemeBox.SelectedIndex = _session.Settings.ThemeMode switch
        {
            ThemeManager.Dark => 1,
            ThemeManager.System => 2,
            _ => 0,
        };
        AccentColorBox.Text = ThemeManager.TryNormalizeAccent(_session.Settings.AccentColor, out var accent)
            ? accent
            : ThemeManager.DefaultAccent;
        AccentPreview.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(AccentColorBox.Text));
        _suppressTheme = false;
        _suppressPick = false;

        RefreshVersionList();
        RefreshJavaList();
        RefreshCatalogStatus();
    }

    /// <summary>Theme changes preview immediately and persist with the floating save button.</summary>
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTheme || ThemeBox is null)
        {
            return;
        }

        _session.Settings.ThemeMode = ThemeBox.SelectedIndex switch
        {
            1 => ThemeManager.Dark,
            2 => ThemeManager.System,
            _ => ThemeManager.Light,
        };
        var accent = ThemeManager.TryNormalizeAccent(AccentColorBox.Text, out var normalized)
            ? normalized
            : _session.Settings.AccentColor;
        ThemeManager.Apply(_session.Settings.ThemeMode, accent);
        SaveStatus.Text = "主题已预览，点「保存设置」后保留";
    }

    private void OnAccentColorChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTheme || AccentPreview is null)
        {
            return;
        }

        if (!ThemeManager.TryNormalizeAccent(AccentColorBox.Text, out var normalized))
        {
            SaveStatus.Text = "主题颜色格式应为 #RRGGBB";
            return;
        }

        AccentPreview.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(normalized));
        ThemeManager.Apply(_session.Settings.ThemeMode, normalized);
        SaveStatus.Text = "颜色已预览，点「保存设置」后保留";
    }

    private void OnAccentPreset(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string value)
        {
            AccentColorBox.Text = value;
        }
    }

    /// <summary>显示服务端地址（只读）、当前数据版本与上次更新时间。</summary>
    private void RefreshCatalogStatus()
    {
        var remote = _session.RemoteCatalog;
        var store = _session.Catalog;
        var updated = store.LastUpdatedAt is { } time ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "（还没成功更新过）";
        CatalogStatus.Text =
            $"当前数据版本：revision {store.Revision}（{store.LoadEntries().Count} 条）\n" +
            $"上次更新时间：{updated}\n" +
            $"最近一次检查：{remote.LastCheckMessage}";
    }

    /// <summary>「立即刷新」：强制向服务端要一次最新目录。</summary>
    private async void OnRefreshCatalog(object sender, RoutedEventArgs e)
    {
        CatalogStatus.Text = "正在向服务端请求最新列表…";
        try
        {
            await _session.RemoteCatalog.CheckAsync(force: true);
            (Window.GetWindow(this) as MainWindow)?.ReloadServersFromCache();
        }
        catch (Exception ex)
        {
            CatalogStatus.Text = $"刷新失败：{ex.Message}";
            return;
        }

        RefreshCatalogStatus();
    }

    /// <summary>内存滑动条：128MB ～ 本机物理内存，步进 128MB，拖动即时同步到输入框。</summary>
    private void ConfigureMemorySlider()
    {
        var physicalMb = (int)(NetCompat.AvailableMemoryBytes() / 1024 / 1024);
        if (physicalMb < 1024)
        {
            physicalMb = 8192; // 取不到物理内存时给个稳妥上限
        }

        var max = Math.Max(128, physicalMb / 128 * 128); // 取整到 128 的倍数
        MemorySlider.Minimum = 128;
        MemorySlider.Maximum = max;
        MemorySlider.Value = NetCompat.Clamp(_session.Settings.MaxMemoryMb, 128, max);
        MemoryRangeText.Text = $"可拖动设置：128 MB ～ {max} MB（本机物理内存 {physicalMb} MB，步进 128 MB）";
    }

    private void OnMemorySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemoryBox is null)
        {
            return; // 初始化阶段控件还没建好
        }

        MemoryBox.Text = ((int)Math.Round(e.NewValue / 128) * 128).ToString();
    }

    private void OnMemoryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (MemorySlider is null || !int.TryParse(MemoryBox.Text?.Trim(), out var value))
        {
            return;
        }

        MemorySlider.Value = NetCompat.Clamp(value, MemorySlider.Minimum, MemorySlider.Maximum);
    }

    private void RefreshVersionList()
    {
        var installed = _session.Installation.GetInstalledVersions(_session.GameRoot);
        var usable = installed.Where(v => v.IsUsable).ToList();
        _suppressPick = true;
        VersionBox.ItemsSource = usable.Select(v => v.Id).ToList();
        VersionBox.SelectedItem = _session.CurrentVersionId;
        if (VersionBox.SelectedIndex < 0 && usable.Count > 0)
        {
            VersionBox.SelectedIndex = 0;
            _session.CurrentVersionId = usable[0].Id;
        }

        _suppressPick = false;
        ShowIncompleteVersionsHint(installed);
    }

    /// <summary>提示那些"只有 jar、没有版本 JSON"的残缺目录（启不了，但容易让人以为版本丢了）。</summary>
    private void ShowIncompleteVersionsHint(IReadOnlyList<Core.Launcher.GameVersionInfo> installed)
    {
        var broken = installed.Where(v => !v.IsUsable).ToList();
        if (broken.Count == 0)
        {
            if (IncompleteVersionsText is not null)
            {
                IncompleteVersionsText.Visibility = Visibility.Collapsed;
            }

            return;
        }

        IncompleteVersionsText.Text =
            $"检测到 {broken.Count} 个不完整的版本目录（缺少版本 JSON，无法启动，也不会出现在版本列表里）：" +
            string.Join("、", broken.Select(v => v.Id)) +
            "。请用对应的加载器安装器补齐版本 JSON，或重新导入自带完整版本的整合包。";
        IncompleteVersionsText.Visibility = Visibility.Visible;
    }

    private void RefreshJavaList()
    {
        _session.EnsureJavaRestored();
        var list = _session.GetJavaRuntimes().ToList();
        var versionId = _session.CurrentVersionId ?? string.Empty;
        var required = JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, versionId);
        // 1.13 之前的老版本只能用 Java 8；1.13 起允许更高的 Java（没 17 就用 21，不必再下）
        var allowsNewer = JavaRuntimeService.AllowsNewerJava(versionId);
        _suppressPick = true;
        JavaBox.ItemsSource = list.Select(j => JavaChoiceText(j, allowsNewer, required)).ToList();
        var index = _session.CurrentJava is null
            ? -1
            : list.FindIndex(j => string.Equals(j.JavaPath, _session.CurrentJava.JavaPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && list.Count > 0)
        {
            // 与启动时同一套规则：完全一致的优先 → 其次"≥ 要求里最小的"
            var best = JavaRuntimeService.PickBest(list, versionId, required);
            index = best is null ? 0 : list.IndexOf(best);
            if (index < 0)
            {
                index = 0;
            }
        }

        JavaBox.SelectedIndex = index;
        if (index >= 0)
        {
            _session.CurrentJava = list[index];
        }

        _suppressPick = false;

        var selected = index >= 0 && index < list.Count ? list[index] : null;
        JavaHintText.Text = selected is null
            ? allowsNewer
                ? $"{versionId} 需要 Java {required}（读自版本 JSON），但本机还没有可用的 Java；点「下载适配 Java」会自动装齐。"
                : $"{versionId} 属于老版本（1.13 之前），只能用 Java 8；本机还没有 Java 8，点「下载适配 Java」会自动装齐。"
            : !allowsNewer
                ? selected.MajorVersion == 8
                    ? $"{versionId} 属于老版本（1.13 之前），只能用 Java 8（Java 9+ 会启动失败）；当前选择满足要求。"
                    : $"{versionId} 属于老版本（1.13 之前），只能用 Java 8（Java 9+ 会启动失败）；当前选的是 Java {selected.MajorVersion}，" +
                      "点「下载适配 Java」会自动装 Java 8。"
                : selected.MajorVersion == required
                    ? $"{versionId} 官方要求 Java {required}（读自版本 JSON）；当前选择正好匹配。"
                    : $"{versionId} 官方要求 Java {required}（读自版本 JSON）；本机没有 Java {required}，" +
                      $"将用 Java {selected.MajorVersion} 启动（1.13 以后允许用更高的 Java，不必再下一份）。";
    }

    /// <summary>Java 下拉里的一项：不适用的选项也列出来（允许手动选），只是后面加一句说明。</summary>
    private static string JavaChoiceText(Core.Launcher.JavaRuntime java, bool allowsNewer, int required)
    {
        var label = $"{java.DisplayName} · {java.JavaPath}";
        if (!allowsNewer && java.MajorVersion != 8)
        {
            return label + "（该版本必须 Java 8，不适用）";
        }

        return allowsNewer && java.MajorVersion < required
            ? label + $"（低于要求的 Java {required}）"
            : label;
    }

    private void OnVersionPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressPick && VersionBox.SelectedItem is string id)
        {
            _session.CurrentVersionId = id;
            RefreshJavaList();
            LauncherReloadRequested?.Invoke();
        }
    }

    private void OnJavaPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPick)
        {
            return;
        }

        // 用下拉框绑定的那份缓存的列表（原来在这里又深扫一遍，既慢、索引还可能和界面对不上）
        var list = _session.GetJavaRuntimes().ToList();
        if (JavaBox.SelectedIndex >= 0 && JavaBox.SelectedIndex < list.Count)
        {
            _session.CurrentJava = list[JavaBox.SelectedIndex];
            LauncherReloadRequested?.Invoke();
        }
    }

    private async void OnMaintenance(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.Tag is not string action)
        {
            return;
        }

        _busy = true;
        // 打开子窗口的操作不在设置页显示进度条（子窗口自己显示），其余维护操作才显示
        var usesSubWindow = action is "mods" or "javadetect" or "javadownload" or "verify";
        MaintenanceProgress.Visibility = usesSubWindow ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            switch (action)
            {
                case "refresh":
                {
                    // 设置页只列出本机已安装版本。
                    var installed = _session.Installation.GetInstalledVersions(_session.GameRoot);
                    _suppressPick = true;
                    VersionBox.ItemsSource = installed.Select(v => v.Id).ToList();
                    VersionBox.SelectedItem = _session.CurrentVersionId is { } current &&
                                              installed.Any(v => string.Equals(v.Id, current, StringComparison.OrdinalIgnoreCase))
                        ? current
                        : installed.FirstOrDefault()?.Id;
                    _suppressPick = false;
                    MaintenanceStatus.Text =
                        $"已刷新：本机已安装 {installed.Count} 个版本。";
                    break;
                }

                case "mods":
                {
                    _busy = false;
                    new ModDownloadWindow { Owner = Window.GetWindow(this) }.ShowDialog();
                    MaintenanceStatus.Text = "已关闭模组下载窗口。";
                    break;
                }

                case "modpack":
                {
                    var dialog = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "选择整合包压缩包",
                        Filter = "整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|所有文件 (*.*)|*.*",
                    };
                    if (dialog.ShowDialog() != true)
                    {
                        return;
                    }

                    MaintenanceStatus.Text = "正在安装整合包…";
                    // 走下载管理器：这样「资源下载」页里能看到整体进度、单文件进度、速度与剩余时间
                    // （CurseForge 的模组地址仍由服务端代理解析）
                    var packSteps = new List<DownloadStep> { new(DownloadCategory.游戏本体, "整合包") };
                    packSteps[0].Status = DownloadStatus.下载中;
                    var packTask = new DownloadTask($"整合包：{System.IO.Path.GetFileName(dialog.FileName)}", packSteps);
                    DownloadManager.Current.Enqueue(packTask, async (progress, token) =>
                    {
                        var result = await _session.Modpacks.InstallAsync(
                            dialog.FileName,
                            _session.GameRoot,
                            instanceId: null,
                            curseForgeApiKey: null,
                            progress: progress,
                            cancellationToken: token,
                            allowVersionDownload: true);
                        return (result.Success, result.Message);
                    });
                    MaintenanceStatus.Text = "已在「资源下载」里开始安装整合包…";
                    var packOk = await packTask.Completion;
                    MaintenanceStatus.Text = packOk
                        ? "整合包安装完成（明细见「资源下载」）。"
                        : "整合包安装失败或被取消，详情见「资源下载」。";
                    RefreshVersionList();
                    break;
                }

                case "javadetect":
                {
                    // 弹独立窗口展示搜索过程，搜索完成后由用户点「完成」关闭
                    var search = new JavaSearchWindow { Owner = Window.GetWindow(this) };
                    search.ShowDialog();
                    RefreshJavaList();
                    MaintenanceStatus.Text = $"Java 检测完成，共 {JavaBox.Items.Count} 个可用运行时。";
                    break;
                }

                case "javadownload":
                {
                    var feature = JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, _session.CurrentVersionId ?? string.Empty);
                    // 独立窗口：直接用已检测到的 Java 列表判断，没有才进入下载流程
                    var download = new JavaDownloadWindow(feature, _session.CurrentVersionId ?? string.Empty)
                    {
                        Owner = Window.GetWindow(this),
                    };
                    download.ShowDialog();
                    if (download.InstalledJava is { } installedJava)
                    {
                        _session.CurrentJava = installedJava;
                        RefreshJavaList();
                    }

                    MaintenanceStatus.Text = download.StatusMessage;
                    break;
                }

                case "openfolder":
                    Directory.CreateDirectory(_session.GameRoot);
                    Process.Start(new ProcessStartInfo(_session.GameRoot) { UseShellExecute = true });
                    break;

                case "verify":
                {
                    // 独立窗口：可在窗口内选择任意已安装版本
                    new IntegrityCheckWindow(_session.CurrentVersionId) { Owner = Window.GetWindow(this) }.ShowDialog();
                    MaintenanceStatus.Text = "完整性检查窗口已关闭。";
                    break;
                }
            }

            LauncherReloadRequested?.Invoke();
        }
        catch (Exception ex)
        {
            MaintenanceStatus.Text = $"操作失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            MaintenanceProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBrowseGameDir(object sender, RoutedEventArgs e)
    {
        // OpenFolderDialog 是 .NET 8 WPF 才有的类型；net48 走 FolderPicker 里的 WinForms 兜底
        var folder = FolderPicker.PickFolder(Window.GetWindow(this), "选择 .minecraft 目录", GameDirBox.Text);
        if (folder is not null)
        {
            GameDirBox.Text = folder;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CacheBox.Text.Trim(), out var seconds) || seconds < 5)
        {
            SaveStatus.Text = "缓存时长需要是不小于 5 的整数";
            return;
        }

        if (!ThemeManager.TryNormalizeAccent(AccentColorBox.Text, out var accent))
        {
            SaveStatus.Text = "主题颜色格式应为 #RRGGBB";
            return;
        }

        if (!int.TryParse(MemoryBox.Text.Trim(), out var memory) || memory < 512)
        {
            memory = 4096;
            MemoryBox.Text = memory.ToString();
        }

        _session.Settings.GameDirectory = string.IsNullOrWhiteSpace(GameDirBox.Text) ? null : GameDirBox.Text.Trim();
        _session.Settings.UseMirror = MirrorBox.IsChecked == true;
        _session.Settings.EnableQuery = QueryBox.IsChecked == true;
        _session.Settings.CacheSeconds = seconds;
        _session.Settings.MaxMemoryMb = memory;
        _session.Settings.VersionIsolation = IsolationBox.IsChecked == true;
        _session.Settings.TrayAfterLaunch = TrayAfterLaunchBox.IsChecked == true;
        _session.Settings.TrayOnClose = TrayOnCloseBox.IsChecked == true;
        _session.Settings.DefaultServerAddress =
            string.IsNullOrWhiteSpace(ServerAddressBox.Text) ? null : ServerAddressBox.Text.Trim();
        _session.Settings.AccentColor = accent;
        _session.Settings.ThemeMode = ThemeBox.SelectedIndex switch
        {
            1 => ThemeManager.Dark,
            2 => ThemeManager.System,
            _ => ThemeManager.Light,
        };
        _session.Settings.Save();
        ThemeManager.Apply(_session.Settings.ThemeMode, accent);
        _session.Installation.SetDownloadSource(_session.Settings.UseMirror);

        SaveStatus.Text = "已保存";
        LauncherReloadRequested?.Invoke();
    }

    private void OnSubmitServer(object sender, RoutedEventArgs e)
    {
        // 用户 2026-10-10：客户端不再自己提交，改成跳官网「服主入口」，
        // 注册/登录后在服主后台认领并提交新服务器。
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.ServerOwnerEntryUrl) { UseShellExecute = true });
            CatalogStatus.Text = "已打开官网「服主入口」：注册/登录后即可在服主后台提交新服务器，审核通过后出现在列表里。";
        }
        catch (Exception ex)
        {
            CatalogStatus.Text = $"打开浏览器失败：{ex.Message}（地址：{AppInfo.ServerOwnerEntryUrl}）";
        }
    }
}
