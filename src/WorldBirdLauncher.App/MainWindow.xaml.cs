using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WorldBirdLauncher.App.Ads;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.App.ViewModels;
using WorldBirdLauncher.Core.Models;
using WorldBirdLauncher.Core.Networking;
using WorldBirdLauncher.Core.Protocol;
using WorldBirdLauncher.Core.Services;

namespace WorldBirdLauncher.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppSession _session = AppSession.Current;
    private readonly AppSettings _settings;
    private readonly LocalStore _store;
    private readonly ServerStatusProvider _statusProvider;

    private readonly List<ServerCardViewModel> _all = [];
    private readonly Dictionary<string, DateTimeOffset> _lastQuery = new(StringComparer.OrdinalIgnoreCase);
    private ServerCardViewModel? _selectedServer;
    private bool _isRefreshing;
    private bool _suppressFilter;
    private CancellationTokenSource? _copyToastCancellation;

    /// <summary>托盘图标（按需创建：启动游戏后 / 关闭按钮收进托盘时）。</summary>
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    /// <summary>本次运行启动的游戏进程；全部退出后才恢复窗口。</summary>
    private readonly List<System.Diagnostics.Process> _gameProcesses = [];

    /// <summary>被跟踪的游戏进程对应的版本信息（闪退时用来收集日志）。</summary>
    private readonly Dictionary<int, (string VersionId, string VersionDirectory)> _gameVersions = [];

    /// <summary>
    /// 轮询游戏进程是否退出。
    ///
    /// 不用 <c>Process.Exited</c>：它在线程池线程触发，回调里既不能直接碰 UI（会抛跨线程异常），
    /// 用同步 <c>Dispatcher.Invoke</c> 回 UI 线程、再 Dispose 该 Process，实测会把 UI 线程卡死
    /// （心跳停止、窗口再也不回来）。轮询只在 UI 线程做，没有跨线程问题。
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _gameWatchTimer =
        new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>筛选/搜索/排序的防抖计时器。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _filterTimer =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>广告刷新（缓存 8 分钟；接口失败静默用缓存，绝不影响列表）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _adsTimer =
        new() { Interval = TimeSpan.FromMinutes(8) };

    /// <summary>当前筛选结果（全量），列表只分批渲染其中的一部分。</summary>
    private List<ServerCardViewModel> _filtered = [];

    private int _renderedCount;

    // ---------- 匿名使用指标（2026-10-09 第五十轮：官网展示"打开次数/平均使用时长"用） ----------

    /// <summary>本次进程启动时间（算会话时长）。</summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    /// <summary>本次运行第一次真正拉起游戏的时间（算"打开→启动游戏"耗时）。</summary>
    private DateTimeOffset? _firstLaunchAt;

    /// <summary>本轮被"没采样就隐藏"规则挡掉的条目数（底部提示用）。</summary>
    private int _hiddenUnsampled;

    private bool _showAllCategories;

    // ---------- 详情页大图查看（2026-10-21） ----------
    private readonly List<string> _viewerImages = new();
    private int _viewerIndex;
    private BitmapImage? _viewerBitmap;
    private double _viewerScale = 1;
    private double _viewerFitScale = 1;
    private double _viewerOffsetX;
    private double _viewerOffsetY;
    private bool _viewerDragging;
    private Point _viewerDragStart;
    private double _viewerDragStartX;
    private double _viewerDragStartY;

    private const int RenderBatchSize = 60;

    /// <summary>true 表示真的退出程序（托盘菜单），而非收进托盘。</summary>
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        PreviewKeyDown += OnWindowPreviewKeyDown;

#if LITE
        Title = AppEdition.ProductName;
        TitleBrandText.Text = AppEdition.ProductName + " · 我的世界服务器列表";
#endif

        // 与「启动器」页共用同一份设置与本地数据
        _settings = _session.Settings;
        _store = _session.Store;

        _statusProvider = new ServerStatusProvider(enableQuery: _settings.EnableQuery);
        BuildChips();

        Settings.LauncherReloadRequested += () => Launcher.ReloadFromSettings();
        // 启动页「启动游戏」下方的提示/进度文字，按用户要求合并到底部提示行右侧显示
        // （左侧 StatusBar 留给"已更新状态 / 共载入 N 个服务器"这类消息，两者不再互相顶掉）
        Launcher.HintChanged += text => LauncherHintBar.Text = text;
        _gameWatchTimer.Tick += (_, _) => PollGameProcesses();
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            RunFilterNow();
        };

        // 后台下载：订阅进度更新悬浮按钮，全部完成时浮出气泡
        DownloadManager.Current.PropertyChanged += (_, _) => UpdateDownloadBadge();
        DownloadManager.Current.AllCompleted += ShowDownloadToast;

        Loaded += async (_, _) =>
        {
            // 恢复上次的排序选择（0 在线人数 / 1 信号 / 2 名称 / 3 热度 / 4 收藏优先）
            _restoringSort = true;
            SortBox.SelectedIndex = NetCompat.Clamp(_settings.LastServerSort, 0, SortBox.Items.Count - 1);
            _restoringSort = false;

            LoadServers();
            _ = CheckCatalogUpdatesAsync(initial: true);
            await RefreshAllStatusesAsync();

            // 付费推广：先补发上次没发出去的事件，再拉一次广告（失败静默，列表照常）
            // 阉割版不展示广告位（用户 2026-10-20 确认），置顶/刷新卡排序仍保留。
            if (AppEdition.ShowsAds)
            {
                _ = _session.Ads.FlushQueueAsync();
                _ = RefreshAdsAsync();
            }

            // 匿名使用指标：记一次"打开启动器"（只带匿名设备号，失败静默）
            _ = _session.RemoteCatalog.ReportMetricAsync("app_open");

            // 排序加成（置顶/刷新卡）：跟状态一起刷新
            await RefreshBoostsAsync();
        };

        // 定时检查目录更新（启动时已查过一次；失败不打扰用户）
        _catalogTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _catalogTimer.Tick += (_, _) => _ = CheckCatalogUpdatesAsync(initial: false);
        _catalogTimer.Start();

        _adsTimer.Tick += (_, _) => _ = RefreshAdsAsync();
        _adsTimer.Start();
        _session.RemoteCatalog.UpdateReady += OnCatalogUpdateReady;
    }

    private readonly DispatcherTimer _catalogTimer;
    private CatalogUpdate? _pendingCatalogUpdate;
    private bool _restoringSort;

    // ---------- 窗口与导航 ----------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    // ---------- 后台下载的悬浮按钮与完成气泡 ----------

    private void UpdateDownloadBadge()
    {
        var manager = DownloadManager.Current;
        if (!manager.HasActive)
        {
            DownloadBadge.Visibility = Visibility.Collapsed;
            return;
        }

        DownloadBadge.Visibility = Visibility.Visible;
        DownloadBadgeText.Text = $"总进度 {manager.OverallPercentage:0}% · {manager.OverallText}";
        DownloadBadgeProgress.Value = manager.OverallPercentage;
    }

    private void OnDownloadBadgeClick(object sender, MouseButtonEventArgs e)
    {
        NavDownload.IsChecked = true; // 进入下载管理界面
    }

    private void ShowDownloadToast()
    {
        var manager = DownloadManager.Current;
        var failed = manager.Tasks.Count(t => t.Status == Services.DownloadStatus.失败);
        DownloadToastText.Text = failed == 0
            ? $"下载已完成（{manager.Tasks.Count(t => t.Status == Services.DownloadStatus.已完成)} 个任务）。点右下角按钮可查看详情。"
            : $"下载结束：{failed} 个任务失败，可在「资源下载」页查看原因。";
        DownloadToast.Visibility = Visibility.Visible;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DownloadToast.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- 托盘 ----------

    /// <summary>启动游戏成功后由启动页调用：按设置收进托盘，并跟踪进程退出以恢复窗口。</summary>
    public void TrackGameProcess(System.Diagnostics.Process process, string? versionId = null, string? versionDirectory = null)
    {
        _gameProcesses.Add(process);
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            try
            {
                _gameVersions[process.Id] = (versionId!, versionDirectory ?? string.Empty);
            }
            catch (InvalidOperationException)
            {
                // 进程已退出，忽略
            }
        }

        _gameWatchTimer.Start();
        App.LogLine($"托盘：开始跟踪游戏进程 pid={process.Id}，TrayAfterLaunch={_settings.TrayAfterLaunch}");

        // 匿名指标：本次运行的"打开启动器 → 真正拉起游戏"耗时（只发一次）
        if (_firstLaunchAt is null)
        {
            _firstLaunchAt = DateTimeOffset.Now;
            var seconds = (int)Math.Round((_firstLaunchAt.Value - _startedAt).TotalSeconds);
            if (seconds >= 0 && seconds < 86400)
            {
                _ = _session.RemoteCatalog.ReportMetricAsync("time_to_launch", seconds);
            }
        }

        if (_settings.TrayAfterLaunch && IsVisible)
        {
            EnsureTrayIcon();
            _trayIcon?.ShowBalloonTip(
                3000,
                "游戏运行中",
                "启动器已最小化到托盘，游戏退出后会自动显示。",
                System.Windows.Forms.ToolTipIcon.Info);
            Hide();
            App.LogLine("托盘：已隐藏主窗口并显示托盘图标");
        }
    }

    /// <summary>在 UI 线程上检查被跟踪的游戏进程，全部退出后把窗口显示回来。</summary>
    private void PollGameProcesses()
    {
        for (var i = _gameProcesses.Count - 1; i >= 0; i--)
        {
            var process = _gameProcesses[i];
            bool exited;
            try
            {
                exited = process.HasExited;
            }
            catch (Exception)
            {
                exited = true; // 句柄已经取不到了，按已退出处理
            }

            if (!exited)
            {
                continue;
            }

            var id = TryGetId(process);
            _gameProcesses.RemoveAt(i);
            int? exitCode = null;
            try
            {
                exitCode = process.ExitCode;
            }
            catch (Exception)
            {
                // 取不到退出码就不判闪退
            }

            if (id is int exitedId && _gameVersions.Remove(exitedId, out var tracked) && exitCode is not null && exitCode != 0)
            {
                var versionDirectory = string.IsNullOrWhiteSpace(tracked.VersionDirectory)
                    ? null
                    : tracked.VersionDirectory;
                ShowCrashReport(tracked.VersionId, versionDirectory, exitCode.Value);
            }

            try
            {
                process.Dispose();
            }
            catch (Exception)
            {
            }

            App.LogLine($"托盘：游戏进程 pid={id} 已退出，剩余 {_gameProcesses.Count} 个");
        }

        if (_gameProcesses.Count > 0)
        {
            return;
        }

        _gameWatchTimer.Stop();

        // 全部游戏都退出了：窗口若还藏在托盘就显示回来（用户已手动打开则不再打扰）
        if (!IsVisible)
        {
            RestoreFromTray();
        }
        else
        {
            DisposeTrayIconIfIdle();
        }
    }

    /// <summary>
    /// 游戏异常退出：把 crash-reports 与 logs/latest.log **整份**复制出来并提醒用户。
    /// </summary>
    private void ShowCrashReport(string versionId, string? versionDirectory, int exitCode)
    {
        var collected = new CrashReportService().Collect(_settings.ResolveGameDirectory(), versionId, versionDirectory);
        if (collected is null)
        {
            Views.MessageWindow.Info(this, "游戏异常退出",
                $"游戏（{versionId}）异常退出，退出码 {exitCode}，但没有找到 crash-reports 或 logs 日志。");
            return;
        }

        var (directory, files) = collected.Value;
        var list = string.Join("\n", files.Take(8).Select(Path.GetFileName));
        if (files.Count > 8)
        {
            list += $"\n…（共 {files.Count} 个文件）";
        }

        var open = Views.MessageWindow.Confirm(this, "游戏异常退出",
            $"游戏（{versionId}）异常退出，退出码 {exitCode}。\n\n" +
            $"已提取完整日志到：\n{directory}\n\n{list}\n\n是否现在打开日志目录？",
            "打开日志目录", "关闭");
        if (open)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.LogLine($"打开崩溃日志目录失败：{ex.Message}");
            }
        }
    }

    private static int TryGetId(System.Diagnostics.Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主界面");
        showItem.Click += (_, _) => RestoreFromTray();
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出程序");
        exitItem.Click += (_, _) =>
        {
            _exiting = true;
            Close();
        };
        menu.Items.Add(showItem);
        menu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = AppEdition.ProductName + " · 我的世界服务器列表",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                RestoreFromTray();
            }
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        Hide();
    }

    private void RestoreFromTray()
    {
        App.LogLine("托盘：恢复主窗口");
        Launcher.ResetLaunchStatus(); // 游戏已退出：清掉上一次启动残留的进度条与提示
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        DisposeTrayIconIfIdle();
    }

    /// <summary>没有游戏在跑、且不需要"关闭到托盘"时，释放托盘图标（不等于退出程序）。</summary>
    private void DisposeTrayIconIfIdle()
    {
        if (_gameProcesses.Count > 0 || _settings.TrayOnClose)
        {
            return;
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    /// <summary>托盘图标：优先用本程序自身图标，取不到就退回系统默认图标。</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var path = NetCompat.ExecutablePath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception)
        {
            // 取不到就用默认图标
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>关闭按钮/Alt+F4：开启"关闭到托盘"时只收进托盘，否则按原样退出。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _settings.TrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // 匿名指标：本次会话时长（打开程序 → 关闭窗口）。退出前等最多 2 秒把它发出去。
        try
        {
            var seconds = (int)Math.Round((DateTimeOffset.Now - _startedAt).TotalSeconds);
            if (seconds >= 0 && seconds < 86400 * 7)
            {
                var task = _session.RemoteCatalog.ReportMetricAsync("app_close", seconds);
                task.Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"上报会话时长失败：{ex.Message}");
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnClosing(e);
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (Launcher is null || ServersPanel is null || Settings is null)
        {
            return;
        }

        var launch = NavLaunch.IsChecked == true;
        var servers = NavServers.IsChecked == true;
        var skin = NavSkin.IsChecked == true;
        var download = NavDownload.IsChecked == true;
        Launcher.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        ServersPanel.Visibility = servers ? Visibility.Visible : Visibility.Collapsed;
        Skin.Visibility = skin ? Visibility.Visible : Visibility.Collapsed;
        Download.Visibility = download ? Visibility.Visible : Visibility.Collapsed;
        Settings.Visibility = (!launch && !servers && !skin && !download) ? Visibility.Visible : Visibility.Collapsed;

        if (!launch && !servers && !skin && !download)
        {
            Settings.Reload();
        }

        // 切到服务器列表页时，如果有"待确认/待应用"的目录更新，按策略处理
        if (servers)
        {
            OnServersPageVisibilityChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ServerCardViewModel> Servers { get; } = [];

    /// <summary>详情页「服务器地址」区块的数据（多线路）。</summary>
    public ObservableCollection<ServerAddressItem> DetailAddresses { get; } = [];

    public ObservableCollection<FilterChipViewModel> VersionChips { get; } = [];

    public ObservableCollection<FilterChipViewModel> LineChips { get; } = [];

    /// <summary>专属客户端：有 / 无（未选择=全部；未标注不进入任一筛选结果）。</summary>
    public ObservableCollection<FilterChipViewModel> CustomClientChips { get; } = [];

    /// <summary>白名单：有 / 无（null=未标注，仅在都不选时显示）。</summary>
    public ObservableCollection<FilterChipViewModel> WhitelistChips { get; } = [];

    /// <summary>正版验证：有 / 无（null=未标注，仅在都不选时显示）。</summary>
    public ObservableCollection<FilterChipViewModel> PremiumChips { get; } = [];

    public ObservableCollection<FilterChipViewModel> LoaderChips { get; } = [];

    public ObservableCollection<FilterChipViewModel> CategoryChips { get; } = [];

    /// <summary>热门模组筛选（科技 / 魔法 / 冒险 …，从介绍关键词归纳）。</summary>
    public ObservableCollection<FilterChipViewModel> ModGroupChips { get; } = [];

    /// <summary>延迟（信号等级）筛选：良好 / 一般 / 较差 / 仅看可用。</summary>
    public ObservableCollection<FilterChipViewModel> LatencyChips { get; } = [];

    /// <summary>"仅看可用"对应的三个等级。</summary>
    private static readonly string[] UsableLevels = [StatusLevels.Good, StatusLevels.Fair, StatusLevels.Poor];

    private const string LatencyUsableChip = "仅看可用";

    public IReadOnlyList<HistoryRecord> History => _store.History;

    public ServerCardViewModel? SelectedServer
    {
        get => _selectedServer;
        private set
        {
            _selectedServer = value;
            OnPropertyChanged(nameof(SelectedServer));
            OnPropertyChanged(nameof(IsDetailVisible));
        }
    }

    public bool IsDetailVisible => SelectedServer is not null;

    private void BuildChips()
    {
        // 线路筛选（2026-10-21 用户要求：地区改为线路；默认只选国内线路，排除海外）
        foreach (var line in new[] { "电信", "联通", "移动", "海外", "未标注" })
        {
            LineChips.Add(new FilterChipViewModel(line, ApplyFilters));
        }

        foreach (var chip in LineChips.Where(c => c.Label is "电信" or "联通" or "移动" or "未标注"))
        {
            chip.IsChecked = true;
        }

        // 三个布尔筛选：有 / 无；都不选 = 全部（未标注只在"全部"里出现）
        AddYesNoChips( CustomClientChips );
        AddYesNoChips( WhitelistChips );
        AddYesNoChips( PremiumChips );

        // 版本 / 玩法按"常用度"排序，默认只显示前几个，其余点「更多》」展开
        foreach (var group in ServerCategories.VersionGroups.OrderBy(PriorityOfVersion))
        {
            VersionChips.Add(new FilterChipViewModel(group, ApplyFilters));
        }

        foreach (var loader in ServerCategories.AllLoaders)
        {
            LoaderChips.Add(new FilterChipViewModel(loader, ApplyFilters));
        }

        var categories = _session.Catalog.LoadEntries().SelectMany(e => e.Categories).Distinct(StringComparer.Ordinal)
            .OrderBy(PriorityOfCategory).ThenBy(c => c, StringComparer.Ordinal);
        foreach (var category in categories)
        {
            CategoryChips.Add(new FilterChipViewModel(category, ApplyFilters));
        }

        // 延迟筛选：前三个按等级多选；"仅看可用"是快捷项（等价于勾选前三个）
        LatencyChips.Add(new FilterChipViewModel("良好", OnLatencyChipChanged));
        LatencyChips.Add(new FilterChipViewModel("一般", OnLatencyChipChanged));
        LatencyChips.Add(new FilterChipViewModel("较差", OnLatencyChipChanged));
        LatencyChips.Add(new FilterChipViewModel(LatencyUsableChip, OnUsableChipChanged));

        RebuildChipVisibility();
    }

    private void AddYesNoChips( ObservableCollection<FilterChipViewModel> chips )
    {
        chips.Add( new FilterChipViewModel( "有", ApplyFilters ) );
        chips.Add( new FilterChipViewModel( "无", ApplyFilters ) );
    }

    /// <summary>勾选"良好/一般/较差"时，同步"仅看可用"的选中状态（三者全选 = 可用）。</summary>
    private void OnLatencyChipChanged()
    {
        _suppressFilter = true;
        try
        {
            var usable = LatencyChips.FirstOrDefault(c => c.Label == LatencyUsableChip);
            var levelChips = LatencyChips.Where(c => c.Label != LatencyUsableChip).ToList();
            if (usable is not null)
            {
                usable.IsChecked = levelChips.Count > 0 && levelChips.All(c => c.IsChecked);
            }
        }
        finally
        {
            _suppressFilter = false;
        }

        ApplyFilters();
    }

    /// <summary>点"仅看可用"= 一次性勾上/取消 良好+一般+较差。</summary>
    private void OnUsableChipChanged()
    {
        var usable = LatencyChips.FirstOrDefault(c => c.Label == LatencyUsableChip);
        var wanted = usable?.IsChecked == true;

        _suppressFilter = true;
        try
        {
            foreach (var chip in LatencyChips.Where(c => c.Label != LatencyUsableChip))
            {
                chip.IsChecked = wanted;
            }
        }
        finally
        {
            _suppressFilter = false;
        }

        ApplyFilters();
    }

    /// <summary>版本筛选的默认展示顺序（越靠前越常用）。</summary>
    private static readonly string[] VersionPriority =
    [
        "26.x", "1.21.x", "1.20.x", "1.19.x", "1.18.x", "1.17.x", "1.16.x", "1.12.x",
        "1.7.x", "1.8.x", "1.9.x", "1.10.x", "1.11.x", "1.13.x", "1.14.x", "1.15.x", "1.6.x",
    ];

    /// <summary>玩法筛选的默认展示顺序（越靠前越常用）。</summary>
    private static readonly string[] CategoryPriority =
        ["模组服", "插件服", "生存", "空岛", "RPG", "小游戏", "建筑", "创造", "PvP", "角色扮演", "宝可梦", "监狱", "无政府"];

    private static int PriorityOfVersion(string group)
    {
        var index = Array.IndexOf(VersionPriority, group);
        return index < 0 ? int.MaxValue : index;
    }

    private static int PriorityOfCategory(string category)
    {
        var index = Array.IndexOf(CategoryPriority, category);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>版本 / 玩法默认只显示常用的几个，点「更多》」展开全部。</summary>
    private void RebuildChipVisibility()
    {
        for (var i = 0; i < VersionChips.Count; i++)
        {
            // 版本已收进顶部弹层，弹层里始终显示全部版本
            VersionChips[i].IsVisible = true;
        }

        const int defaultCategoryCount = 8;
        for (var i = 0; i < CategoryChips.Count; i++)
        {
            CategoryChips[i].IsVisible = _showAllCategories || i < defaultCategoryCount;
        }
    }

    private void OnMoreVersions(object sender, RoutedEventArgs e)
    {
        RebuildChipVisibility();
        VersionPopup.IsOpen = !VersionPopup.IsOpen;
    }

    private void OnClearVersions(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        foreach (var chip in VersionChips)
        {
            chip.IsChecked = false;
        }

        _suppressFilter = false;
        ApplyFilters();
    }

    private void OnMoreCategories(object sender, RoutedEventArgs e)
    {
        _showAllCategories = !_showAllCategories;
        MoreCategoriesButton.Content = _showAllCategories ? "收起 ˄" : "更多 ˅";
        RebuildChipVisibility();
    }

    /// <summary>
    /// 顶部「筛选」按钮：展开/收起高级筛选抽屉。抽屉位于列表滚动区内，
    /// 会随服务器卡片一起滚动（用户 2026-10-21 要求：不要固定）。
    /// </summary>
    private void OnToggleFilterDrawer(object sender, RoutedEventArgs e)
    {
        var expand = FilterDrawer.Visibility != Visibility.Visible;
        FilterDrawer.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        FilterDrawerButton.Content = expand ? "筛选 ˄" : "筛选 ˅";
    }

    private void LoadServers()
    {
        // 服务器列表只走远端下发的本地缓存（内置数据已不再是数据源）
        var entries = _session.Catalog.LoadEntries();
        RebuildServers(entries);
        UpdateEmptyState(entries.Count == 0);
        StatusBar.Text = entries.Count == 0
            ? "还没有服务器数据：正在向服务端请求…"
            : $"共载入 {_all.Count} 个服务器（数据版本 {_session.Catalog.Revision}），正在查询状态…";
    }

    /// <summary>
    /// 检查目录更新（启动时 / 每 6 小时 / 设置页「立即刷新」都走这里）。
    /// 更新已在后台落盘，是否立刻应用由 <see cref="OnCatalogUpdateReady"/> 按"用户是否在看列表页"决定。
    /// </summary>
    public async Task<bool> CheckCatalogUpdatesAsync(bool initial)
    {
        var changed = await _session.RemoteCatalog.CheckAsync(force: false);
        if (!changed)
        {
            if (initial && !_session.Catalog.HasCache)
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateEmptyState(true);
                    StatusBar.Text = "无法连接服务器列表服务端，且本地没有缓存数据。请检查网络或稍后点「立即刷新」。";
                });
            }
        }

        return changed;
    }

    private void OnCatalogUpdateReady(CatalogUpdate update)
    {
        // 事件在后台线程触发，切回 UI 线程处理
        Dispatcher.Invoke(() =>
        {
            if (update.IsEmpty)
            {
                StatusBar.Text = $"服务器列表数据已检查（revision {update.ToRevision}，无条目变化）。";
                return;
            }

            if (NavServers.IsChecked == true)
            {
                // 用户正在看列表页 → 先问一句
                _pendingCatalogUpdate = update;
                StatusBar.Text = $"服务器列表有更新（+{update.Added} / -{update.Removed} 条），等待你确认后应用。";
            }
            else
            {
                ApplyCatalogUpdate(update);
                StatusBar.Text = $"已静默更新服务器列表数据：revision {update.ToRevision}（+{update.Added} / -{update.Removed}）。";
            }
        });
    }

    private void ApplyCatalogUpdate(CatalogUpdate update)
    {
        _pendingCatalogUpdate = null;
        LoadServers();

        // ★ 首次启动的 BUG：本地还没有目录时，之前那一轮状态请求是**对着空列表**跑的，
        //   所以卡片上"人数/信号"一直是空的，必须手动点一次「刷新状态」才出来。
        //   目录一到位就自动补一次状态刷新（结果一样，但用户不用手动点）。
        _ = RefreshAllStatusesAsync();
    }

    /// <summary>离开/进入服务器列表页时，把"待确认"的更新按新策略处理。</summary>
    private void OnServersPageVisibilityChanged()
    {
        var update = _pendingCatalogUpdate;
        if (update is null)
        {
            return;
        }

        if (NavServers.IsChecked != true)
        {
            // 用户离开列表页 → 直接应用（不打断他）
            ApplyCatalogUpdate(update);
            return;
        }

        var applied = Views.MessageWindow.Confirm(
            this,
            "服务器列表有更新",
            $"服务端下发了新的服务器列表数据（revision {update.ToRevision}）。\n\n" +
            $"新增 {update.Added} 条，下架 {update.Removed} 条"
            + (string.IsNullOrWhiteSpace(update.Note) ? string.Empty : $"\n说明：{update.Note}")
            + "\n\n是否立即应用？（选「稍后」会在你离开本页时自动应用）",
            "立即应用",
            "稍后");
        if (applied)
        {
            ApplyCatalogUpdate(update);
        }
    }

    /// <summary>无缓存时的空列表提示（有一个「重试」按钮）。</summary>
    private void UpdateEmptyState(bool empty)
    {
        ListEmptyOverlay.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>让设置页的「立即刷新」等入口直接按本地缓存重建列表。</summary>
    public void ReloadServersFromCache() => LoadServers();

    /// <summary>空状态里的「重试」：立刻再查一次服务端。</summary>
    private async void OnRetryCatalog(object sender, RoutedEventArgs e)
    {
        StatusBar.Text = "正在向服务端请求服务器列表…";
        var changed = await CheckCatalogUpdatesAsync(initial: true);
        if (changed || _session.Catalog.HasCache)
        {
            LoadServers();
            await RefreshAllStatusesAsync();
        }
        else
        {
            ListEmptyHint.Text = $"仍然连不上 {_session.RemoteCatalog.Endpoint}\n"
                                 + "请检查网络连接或稍后重试。";
        }
    }

    /// <summary>用给定数据重建列表（远程数据或内置数据）。</summary>
    private void RebuildServers(IReadOnlyList<ServerEntry> entries)
    {
        _all.Clear();
        _lastQuery.Clear();
        var retainedIds = _session.Catalog.LoadRetained()
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var card = new ServerCardViewModel(entry) { IsFavorite = _store.IsFavorite(entry.Id) };
            if (retainedIds.Contains(entry.Id))
            {
                card.MarkRetained(); // 已下架但保留：显示为"离线"
            }

            if (_session.RemoteCatalog.Boosts.TryGetValue(entry.Id, out var boost))
            {
                card.ApplyBoost(boost.Kind, boost.Rank, boost.Until, boost.LastRefreshedAt);
            }

            _all.Add(card);
        }

        // 热门模组分组：从所有条目的介绍关键词归纳，供筛选用
        var modGroups = _all.SelectMany(v => v.ModGroups).Distinct(StringComparer.Ordinal).OrderBy(g => g);
        if (ModGroupChips.Count == 0)
        {
            foreach (var group in modGroups)
            {
                ModGroupChips.Add(new FilterChipViewModel(group, ApplyFilters));
            }
        }

        ApplyFilters();
    }

    /// <summary>筛选/排序入口：250ms 防抖，避免连点筛选或输入时反复重建列表。</summary>
    private void ApplyFilters()
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void RunFilterNow()
    {
        if (_suppressFilter)
        {
            return;
        }

        // 先让 loading 遮罩渲染出来，再做耗时的列表更新
        ListBusyOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                ApplyFiltersCore();
            }
            finally
            {
                ListBusyOverlay.Visibility = Visibility.Collapsed;
            }
        }));
    }

    private void ApplyFiltersCore()
    {
        if (_suppressFilter)
        {
            return;
        }

        // 本机实测信号：把缓存贴到卡片上（列表显示"本机 ↔ 游戏服务器"的信号）
        ApplyLocalLatency();

        var keyword = SearchBox?.Text?.Trim() ?? string.Empty;
        var versions = VersionChips.Where(c => c.IsChecked).Select(c => c.Label).ToHashSet(StringComparer.Ordinal);
        var lines = LineChips.Where(c => c.IsChecked).Select(c => c.Label).ToHashSet(StringComparer.Ordinal);
        var loaders = LoaderChips.Where(c => c.IsChecked).Select(c => c.Label).ToHashSet(StringComparer.Ordinal);
        var categories = CategoryChips.Where(c => c.IsChecked).Select(c => c.Label).ToHashSet(StringComparer.Ordinal);
        var modGroups = ModGroupChips.Where(c => c.IsChecked).Select(c => c.Label).ToHashSet(StringComparer.Ordinal);
        var customYes = CustomClientChips.Any(c => c.Label == "有" && c.IsChecked);
        var customNo = CustomClientChips.Any(c => c.Label == "无" && c.IsChecked);
        var whitelistYes = WhitelistChips.Any(c => c.Label == "有" && c.IsChecked);
        var whitelistNo = WhitelistChips.Any(c => c.Label == "无" && c.IsChecked);
        var premiumYes = PremiumChips.Any(c => c.Label == "有" && c.IsChecked);
        var premiumNo = PremiumChips.Any(c => c.Label == "无" && c.IsChecked);
        // 延迟筛选：把中文 chip 映射回等级；"仅看可用"等价于勾了 良好+一般+较差
        var latencyLevels = new HashSet<string>(StringComparer.Ordinal);
        if (LatencyChips.Any(c => c.Label == "良好" && c.IsChecked))
        {
            latencyLevels.Add(StatusLevels.Good);
        }

        if (LatencyChips.Any(c => c.Label == "一般" && c.IsChecked))
        {
            latencyLevels.Add(StatusLevels.Fair);
        }

        if (LatencyChips.Any(c => c.Label == "较差" && c.IsChecked))
        {
            latencyLevels.Add(StatusLevels.Poor);
        }

        var latencyFilterActive = latencyLevels.Count > 0;
        var favoritesOnly = FavoritesOnly?.IsChecked == true;

        IEnumerable<ServerCardViewModel> query = _all;

        if (keyword.Length > 0)
        {
            query = query.Where(v =>
                v.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                v.Address.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                v.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(v.Entry.LongDescription) &&
                 v.Entry.LongDescription!.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                v.ModGroups.Any(g => g.Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
                v.Categories.Any(c => c.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        }

        if (versions.Count > 0)
        {
            query = query.Where(v => v.Entry.VersionGroups.Any(versions.Contains));
        }

        if (lines.Count > 0)
        {
            query = query.Where(v =>
            {
                // 没有线路标注的服务器：国内归"未标注"，国外归"海外"（默认线路筛选会排除海外）
                IEnumerable<string> effective = v.Entry.Lines.Count > 0
                    ? v.Entry.Lines
                    : new[]
                    {
                        string.Equals(v.Entry.Region, ServerCategories.RegionDomestic, StringComparison.Ordinal)
                            ? "未标注"
                            : "海外",
                    };
                return effective.Any(lines.Contains);
            });
        }

        if (customYes ^ customNo)
        {
            var wanted = customYes;
            query = query.Where(v => v.RequiresCustomClient == wanted);
        }

        if (whitelistYes && whitelistNo)
        {
            query = query.Where(v => v.Entry.Whitelist is true or false);
        }
        else if (whitelistYes)
        {
            query = query.Where(v => v.Entry.Whitelist == true);
        }
        else if (whitelistNo)
        {
            query = query.Where(v => v.Entry.Whitelist == false);
        }

        if (premiumYes && premiumNo)
        {
            query = query.Where(v => v.Entry.RequirePremium is true or false);
        }
        else if (premiumYes)
        {
            query = query.Where(v => v.Entry.RequirePremium == true);
        }
        else if (premiumNo)
        {
            query = query.Where(v => v.Entry.RequirePremium == false);
        }

        if (loaders.Count > 0)
        {
            // 互斥：勾「原版」时不显示模组服（模组服有自己的玩法标签，不该出现在原版筛选里）
            var wantsVanilla = loaders.Contains(ServerCategories.LoaderVanilla);
            query = query.Where(v => loaders.Contains(v.Loader) &&
                                     !(wantsVanilla && v.Categories.Contains(ServerCategories.Modded)));
        }

        if (categories.Count > 0)
        {
            query = query.Where(v => v.Categories.Any(categories.Contains));
        }

        if (modGroups.Count > 0)
        {
            query = query.Where(v => v.ModGroups.Any(modGroups.Contains));
        }

        if (favoritesOnly)
        {
            query = query.Where(v => v.IsFavorite);
        }

        if (latencyFilterActive)
        {
            // 选了延迟筛选后：未公开地址、离线、待采样都不显示（用户要求）
            // 注意用**显示用等级**（本机实测优先），否则"筛良好"会和卡片上写的等级对不上。
            query = query.Where(v => latencyLevels.Contains(v.DisplaySignalLevel));
        }

        // ★ 用户 2026-10-08 要求：没搜索时**隐藏"没有采样结果"的服务器**（待采样 + 未公开地址），
        //   离线（采样过、就是连不上）保留；一搜索就把它们放出来（"除非直接搜索"）。
        //   注意要在"延迟筛选"之后做：延迟筛选本身已经排除了这两类。
        if (keyword.Length == 0)
        {
            var unsampled = query
                .Where(v => v.SignalLevel is StatusLevels.Unknown or StatusLevels.NoAddress)
                .Select(v => v.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _hiddenUnsampled = unsampled.Count;
            query = query.Where(v => !unsampled.Contains(v.Id));
        }
        else
        {
            _hiddenUnsampled = 0;
        }

        // 排序（用户 2026-10-10 定稿）：每种排序先分**档**（同名次区间），
        // 付费加成（置顶卡）只能在**自己的档内前移**，绝不越档——
        // 例：按名称排序时，置顶的服务器最多排到"相同首字符"的最前面；按信号排序时最多排到"同档信号"的最前面。
        // 刷新卡不再参与这里的排序：它给的是一笔综合热度分（见 HeatOf），只影响「按综合热度」。
        IOrderedEnumerable<ServerCardViewModel> Tiered(
            IEnumerable<ServerCardViewModel> source, Func<ServerCardViewModel, int> tier) =>
            source.OrderBy(tier)
                .ThenByDescending(v => v.BoostKind == "top" ? 2 : v.BoostKind == "refresh" ? 1 : 0)
                .ThenBy(v => v.BoostRank == 0 ? int.MaxValue : v.BoostRank);

        query = SortBox?.SelectedIndex switch
        {
            // 按信号：这里是**本机实测**的信号（没有实测才退回服务端采样等级）
            1 => Tiered(query, v => StatusLevels.Rank(v.DisplaySignalLevel))
                      .ThenByDescending(v => v.SampledPlayers ?? -1)
                      .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
            // 按名称：相同首字符算同一档
            2 => query.OrderBy(v => NameTier(v.Name))
                      .ThenByDescending(v => v.BoostKind == "top" ? 2 : v.BoostKind == "refresh" ? 1 : 0)
                      .ThenBy(v => v.BoostRank == 0 ? int.MaxValue : v.BoostRank)
                      .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
            // 按综合热度：5 档；刷新卡的加分直接体现在分数里，所以档内按分数排
            3 => query.OrderBy(v => HeatTier(HeatOf(v.Id)))
                      .ThenByDescending(v => v.BoostKind == "top" ? 1 : 0)
                      .ThenBy(v => v.BoostRank == 0 ? int.MaxValue : v.BoostRank)
                      .ThenByDescending(v => HeatOf(v.Id)),
            // 收藏优先：收藏/非收藏两档
            4 => Tiered(query, v => v.IsFavorite ? 0 : 1)
                      .ThenByDescending(v => v.SampledPlayers ?? -1)
                      .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
            // 按在线人数：6 档
            _ => Tiered(query, v => PlayerTier(v.SampledPlayers))
                      .ThenByDescending(v => v.SampledPlayers ?? -1)
                      .ThenBy(v => StatusLevels.Rank(v.DisplaySignalLevel))
                      .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase),
        };

        _settings.LastServerSort = SortBox?.SelectedIndex ?? 0;

        // 广告位（需求 5.2.4）：只在**没有筛选/搜索**时插入；结果 <10 条不插，<40 条只插第 4 条之后。
        // 插入的是额外的推广卡，不占用自然条数、不参与排序。
        //
        // 默认线路是「电信/联通/移动/未标注」（排除海外）；这组默认值不算"用户主动筛选"，
        // 否则完整版的广告位会永远不出现。用户改动线路、搜索、勾任何筛选/收藏/延迟后，广告位消失。
        var natural = query.ToList();
        var defaultLines = new HashSet<string>(StringComparer.Ordinal) { "电信", "联通", "移动", "未标注" };
        var lineFiltering = lines.Count > 0 && !lines.SetEquals(defaultLines);
        var booleanFiltering = customYes || customNo || whitelistYes || whitelistNo || premiumYes || premiumNo;
        var isFiltering = keyword.Length > 0 || versions.Count > 0 || lineFiltering || booleanFiltering ||
                          loaders.Count > 0 || categories.Count > 0 || modGroups.Count > 0 ||
                          latencyFilterActive || favoritesOnly;

        // 增量渲染：先只渲染一屏多一点，滚动到底再继续加载，避免一次创建几百个卡片造成卡顿
        _filtered = isFiltering ? natural : InsertAdCards(natural);
        _renderedCount = 0;
        Servers.Clear();
        LoadMoreServerCards();

        if (FilterResultText is not null)
        {
            FilterResultText.Text = $"筛选结果：{_filtered.Count}个";
        }

        if (VersionButton is not null)
        {
            VersionButton.Content = versions.Count == 0 ? "版本 ˅" : $"版本 {versions.Count} ˅";
        }

        // 渲染完这一屏之后，后台补测"还没有本机测速结果 / 服务端等级变了"的卡片。
        // 放在这里是因为 ApplyFilters() 是走 Dispatcher 异步执行的，
        // 从外面调用时 _filtered 可能还没更新，会漏测（实测第一屏一台都没测到）。
        _ = ProbeLocalLatencyAsync();
    }

    // ---------- 付费推广：拉广告 / 插广告位 / 事件上报 ----------

    /// <summary>拉一次"排序加成"（置顶/刷新卡），应用到所有卡片后重排列表。</summary>
    private async Task RefreshBoostsAsync()
    {
        try
        {
            if (!await _session.RemoteCatalog.RefreshBoostsAsync())
            {
                return;
            }

            ApplyBoosts();
            ApplyFilters();
        }
        catch (Exception ex)
        {
            App.LogLine($"刷新排序加成失败：{ex.Message}");
        }
    }

    /// <summary>把服务端的加成信息贴到卡片上（没有加成的清空）。</summary>
    private void ApplyBoosts()
    {
        foreach (var vm in _all)
        {
            if (_session.RemoteCatalog.Boosts.TryGetValue(vm.Id, out var boost))
            {
                vm.ApplyBoost(boost.Kind, boost.Rank, boost.Until, boost.LastRefreshedAt);
            }
            else
            {
                vm.ApplyBoost(null, 0, null, null);
            }
        }
    }

    /// <summary>拉一次广告；有变化就重排列表（失败静默，不影响自然列表）。</summary>
    private async Task RefreshAdsAsync()
    {
        if (!AppEdition.ShowsAds)
        {
            return;
        }

        try
        {
            var changed = await _session.Ads.RefreshAsync();
            // 首页热门模块的推广位也要跟着更新（不然只有列表页会变）
            Launcher?.NotifyAdsUpdated();
            if (changed)
            {
                ApplyFilters();
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"刷新广告失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按需求文档 5.2.4 把推广卡插到自然结果里：
    /// 结果 ≥40 → 第 4 / 20 / 40 条之后各 1 个；结果 ≥10 → 只插第 4 条之后；结果 &lt;10 → 一个都不插。
    /// </summary>
    private List<ServerCardViewModel> InsertAdCards(IReadOnlyList<ServerCardViewModel> natural)
    {
        if (!AppEdition.ShowsAds)
        {
            return natural.ToList();
        }

        var combined = new List<ServerCardViewModel>(natural.Count + 3);
        if (natural.Count < 10)
        {
            combined.AddRange(natural);
            return combined;
        }

        var slots = new List<(int Position, string Slot)> { (4, "list_after_4") };
        if (natural.Count >= 40)
        {
            slots.Add((20, "list_after_20"));
            slots.Add((40, "list_after_40"));
        }

        var ads = slots.ToDictionary(
            item => item.Slot,
            item => _session.Ads.ForSlot(item.Slot, 1).FirstOrDefault());

        for (var index = 0; index < natural.Count; index++)
        {
            combined.Add(natural[index]);
            var position = index + 1;
            foreach (var (slotPosition, slot) in slots)
            {
                if (slotPosition == position && ads.TryGetValue(slot, out var ad) && ad is not null)
                {
                    combined.Add(CreateAdCard(ad));
                }
            }
        }

        return combined;
    }

    /// <summary>把一条广告包成服务器卡片（与自然卡片同一套模板，右上角显示「推广」）。</summary>
    private ServerCardViewModel CreateAdCard(AdItem ad)
    {
        var entry = new ServerEntry
        {
            Id = string.IsNullOrWhiteSpace(ad.ServerId) ? $"ad-{ad.CampaignId}" : ad.ServerId,
            Name = ad.Title,
            Address = ad.ServerIp ?? string.Empty,
            Port = ad.ServerPort ?? 25565,
            Version = string.Empty,
            Loader = string.Empty,
            Description = ad.Description ?? string.Empty,
            Categories = ad.Tags ?? [],
            IconUrl = ad.IconUrl,
            OfficialSite = ad.TargetUrl,
            IsBuiltIn = false,
        };
        var card = new ServerCardViewModel(entry);
        card.MarkAsAd(ad);

        // 曝光：卡片渲染出来并停留 ≥1 秒才计一次（服务端再按"同设备 24 小时只计一次"去重）
        _ = ReportExposureAsync(card);
        return card;
    }

    private async Task ReportExposureAsync(ServerCardViewModel card)
    {
        try
        {
            await Task.Delay(1000);
            if (card.Ad is null || !Servers.Contains(card))
            {
                return;
            }

            _session.Ads.Report(card.Ad, "exposure");
        }
        catch (Exception)
        {
            // 曝光上报失败不影响界面
        }
    }

    private void OnAdNotInterested(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServerCardViewModel card || card.Ad is null)
        {
            return;
        }

        _session.Ads.Hide(card.Ad);
        StatusBar.Text = "已隐藏这条推广（7 天内不再显示）。";
        ApplyFilters();
    }

    private async Task RefreshAllStatusesAsync(bool force = false)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        RefreshProgress.Visibility = Visibility.Visible;
        RefreshStatusButton.IsEnabled = false;
        try
        {
            var cacheWindow = TimeSpan.FromSeconds(Math.Max(5, _settings.CacheSeconds));
            var targets = _all.Where(v =>
            {
                // 没有直连地址的条目（需专属客户端/官网/QQ群）没法 ping，直接标出来
                if (!v.Entry.HasAddress)
                {
                    return false;
                }

                if (force || !_lastQuery.TryGetValue(v.Id, out var last))
                {
                    return true;
                }

                return DateTimeOffset.Now - last > cacheWindow;
            }).ToList();

            foreach (var vm in _all.Where(v => !v.Entry.HasAddress))
            {
                // 按需求：没有公开直连地址的条目显示「未公开地址」，不参与采样
                vm.ApplyServerStatus(StatusLevels.NoAddress, null, null, null);
            }

            // 状态改由服务端采样下发：客户端只取"信号等级"（不给具体延迟），
            // 具体 ms 在详情页点「测试线路延迟」时由本机实测。
            var statuses = await _session.RemoteCatalog.GetStatusesAsync(targets.Select(v => v.Id));
            var byId = statuses.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var vm in targets)
            {
                _lastQuery[vm.Id] = DateTimeOffset.Now;
                byId.TryGetValue(vm.Id, out var status);
                vm.ApplyServerStatus(
                    status?.Level ?? StatusLevels.Unknown,
                    status?.PlayersOnline,
                    status?.PlayersMax,
                    status?.VersionText,
                    status?.Motd);
            }

            ApplyFilters();
            var sampled = byId.Count;
            StatusBar.Text = sampled == 0
                ? $"服务端还没采样到状态（每小时采样一次），共 {_all.Count} 个服务器。"
                : $"状态已从服务端更新（{DateTime.Now:HH:mm:ss}）：{sampled} 台有采样结果，共 {_all.Count} 个服务器。";
        }
        finally
        {
            RefreshProgress.Visibility = Visibility.Collapsed;
            RefreshStatusButton.IsEnabled = true;
            _isRefreshing = false;
        }
    }

    private async void OnRefreshStatus(object sender, RoutedEventArgs e) => await RefreshAllStatusesAsync(force: true);

    /// <summary>排序用热度：优先服务端全站榜单，其次本地热度。</summary>
    private double HeatOf(string serverId)
    {
        var serverScore = _session.RemoteCatalog.HeatRanking
            .FirstOrDefault(x => string.Equals(x.Id, serverId, StringComparison.OrdinalIgnoreCase))?.Score ?? 0;
        var localScore = HeatService.Current.Score(serverId, _store.IsFavorite(serverId));
        // 刷新卡给的是"综合热度分"的一笔加分（服务端按 30 天半衰期算好 HeatBonus 下发），
        // 用户 2026-10-10 确认：刷新卡只影响「按综合热度」，所以只加在这里。
        var boostBonus = _session.RemoteCatalog.Boosts.TryGetValue(serverId, out var boost) ? boost.HeatBonus : 0;
        return Math.Max(serverScore, localScore) + boostBonus;
    }

    /// <summary>「按在线人数」的 6 档（0 = 人最多）。</summary>
    private static int PlayerTier(int? players) => players switch
    {
        >= 1000 => 0,
        >= 200 => 1,
        >= 50 => 2,
        >= 10 => 3,
        >= 1 => 4,
        _ => 5,
    };

    /// <summary>「按综合热度」的 5 档（0 = 最热）。</summary>
    private static int HeatTier(double score) => score switch
    {
        >= 8 => 0,
        >= 4 => 1,
        >= 2 => 2,
        >= 0.5 => 3,
        _ => 4,
    };

    /// <summary>「按名称」的档位：相同首字符算同一档。</summary>
    private static string NameTier(string? name) =>
        string.IsNullOrEmpty(name) ? "\uFFFF" : char.ToUpperInvariant(name![0]).ToString();

    // ---------- 本机实测信号（用户 2026-10-10） ----------

    private readonly LocalLatencyCache _latency = new();
    private bool _probingLatency;

    /// <summary>把本机测速缓存贴到卡片上；服务端等级变了就当缓存失效（清掉，等重测）。</summary>
    private void ApplyLocalLatency()
    {
        foreach (var vm in _all)
        {
            var cached = _latency.Get(vm.Id);
            if (cached is not null && !string.Equals(cached.ServerLevel, vm.SignalLevel, StringComparison.Ordinal))
            {
                vm.ApplyLocalLatency(null, null, null);
                continue;
            }

            vm.ApplyLocalLatency(cached?.Level, cached?.Milliseconds, cached?.MeasuredAt);
        }
    }

    /// <summary>
    /// 后台补测：只测"已渲染 + 有直连地址 + 服务端已有采样等级 + 缓存缺失/失效"的卡片，
    /// 并发 4、一轮最多 24 台；测完重新排序（用户确认的"后台预 ping + 滚动补测"）。
    /// </summary>
    private async Task ProbeLocalLatencyAsync()
    {
        if (_probingLatency)
        {
            return;
        }

        _probingLatency = true;
        try
        {
            var candidates = _filtered
                .Take(Math.Max(_renderedCount, 24))
                .Where(v => !v.IsAd && v.Entry.HasAddress && NeedsLocalProbe(v))
                .Take(24)
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            using var gate = new SemaphoreSlim(4, 4);
            await Task.WhenAll(candidates.Select(async vm =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (level, milliseconds) = await LocalLatencyProbe
                        .MeasureAsync(vm.Entry, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    var entry = new LocalLatencyEntry(vm.Id, level, milliseconds, vm.SignalLevel, DateTimeOffset.Now);
                    _latency.Set(entry);
                    await Dispatcher.InvokeAsync(() =>
                        vm.ApplyLocalLatency(entry.Level, entry.Milliseconds, entry.MeasuredAt));
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            await Dispatcher.InvokeAsync(ApplyFiltersCore);
        }
        catch (Exception ex)
        {
            App.LogLine($"本机测速失败：{ex.Message}");
        }
        finally
        {
            _probingLatency = false;
        }
    }

    private bool NeedsLocalProbe(ServerCardViewModel vm)
    {
        // 服务端都还没采样到（待采样/未公开地址）就先不测，等状态到位再说
        if (vm.SignalLevel is StatusLevels.Unknown or StatusLevels.NoAddress)
        {
            return false;
        }

        var cached = _latency.Get(vm.Id);
        return cached is null || !string.Equals(cached.ServerLevel, vm.SignalLevel, StringComparison.Ordinal);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilters();

    /// <summary>滚动接近底部时继续渲染下一批卡片。</summary>
    private void OnServerListScroll(object sender, ScrollChangedEventArgs e)
    {
        // 回到置顶按钮：滚动超过一屏才出现
        if (BackToTopButton is not null)
        {
            BackToTopButton.Visibility = e.VerticalOffset > 240 ? Visibility.Visible : Visibility.Collapsed;
        }

        if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 300)
        {
            LoadMoreServerCards();
            // 滚动补测：新渲染出来的卡片按需在本机 ping 一次（只测缓存缺失/失效的）
            _ = ProbeLocalLatencyAsync();
        }
    }

    private void OnBackToTop(object sender, RoutedEventArgs e)
    {
        ServerListScroll.ScrollToTop();
        BackToTopButton.Visibility = Visibility.Collapsed;
    }

    private void LoadMoreServerCards()
    {
        if (_renderedCount >= _filtered.Count)
        {
            UpdateListFooter();
            return;
        }

        var batch = _filtered.Skip(_renderedCount).Take(RenderBatchSize).ToList();
        foreach (var item in batch)
        {
            Servers.Add(item);
        }

        _renderedCount = Math.Min(_filtered.Count, _renderedCount + RenderBatchSize);
        UpdateListFooter();
        LoadIconsForBatch(batch);
    }

    /// <summary>
    /// 只给刚渲染出来的这批卡片按需取图标（服务端 → 本地缓存）；
    /// 拿不到就保持首字母占位（服务端保留过历史图标的条目会命中缓存）。
    /// </summary>
    private void LoadIconsForBatch(IReadOnlyList<ServerCardViewModel> batch)
    {
        foreach (var card in batch)
        {
            if (!card.Entry.HasAddress)
            {
                continue; // 没有直连地址的条目服务端也拿不到图标
            }

            _ = Task.Run(async () =>
            {
                var bytes = await _session.Icons.GetAsync(card.Id);
                if (bytes is null || bytes.Length == 0)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    card.ApplyIconBytes(bytes);
                    if (ReferenceEquals(SelectedServer, card))
                    {
                        OnPropertyChanged(nameof(SelectedServer));
                    }
                });
            });
        }
    }

    private void UpdateListFooter()
    {
        if (ListFooter is null)
        {
            return;
        }

        // 被"没采样就隐藏"挡掉的条目：底部提示里说明一下，免得用户以为服务器丢了
        var hiddenHint = _hiddenUnsampled > 0
            ? $"　·　已隐藏 {_hiddenUnsampled} 台没有采样结果的服务器（离线不隐藏；在搜索框里搜就能看到）"
            : string.Empty;

        if (_filtered.Count == 0)
        {
            ListFooter.Text = _hiddenUnsampled > 0
                ? $"当前筛选下没有已采样的服务器{hiddenHint}"
                : "没有匹配的服务器";
            return;
        }

        ListFooter.Text = (_renderedCount < _filtered.Count
            ? $"已显示 {_renderedCount} / {_filtered.Count} 个服务器（滚动到底部继续加载）"
            : $"共 {_filtered.Count} 个服务器") + hiddenHint;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        // 注意：XAML 初始化时（ComboBoxItem 的 IsSelected）也会触发一次 SelectionChanged，
        // 那时 _settings 还没赋值，必须提前返回，否则启动即崩。
        if (_restoringSort || _settings is null)
        {
            return;
        }

        _settings.LastServerSort = SortBox.SelectedIndex;
        _settings.Save();
        ApplyFilters();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilters();

    private void OnResetFilters(object sender, RoutedEventArgs e)
    {
        _suppressFilter = true;
        SearchBox.Text = string.Empty;
        FavoritesOnly.IsChecked = false;
        foreach (var chip in VersionChips.Concat(LineChips).Concat(LoaderChips).Concat(CategoryChips)
                     .Concat(ModGroupChips).Concat(LatencyChips).Concat(CustomClientChips)
                     .Concat(WhitelistChips).Concat(PremiumChips))
        {
            chip.IsChecked = false;
        }

        // 恢复默认线路：国内全部 + 未标注，排除海外
        foreach (var chip in LineChips.Where(c => c.Label is "电信" or "联通" or "移动" or "未标注"))
        {
            chip.IsChecked = true;
        }

        SortBox.SelectedIndex = 0;
        _suppressFilter = false;
        ApplyFilters();
        StatusBar.Text = "已重置筛选条件。";
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        // 详情已打开时，任何卡片点击只关闭详情，不再打开另一台（用户 2026-10-21 要求）
        if (DetailOverlay.Visibility == Visibility.Visible)
        {
            OnCloseDetail(sender, e);
            e.Handled = true;
            return;
        }

        ShowDetail((sender as FrameworkElement)?.Tag as ServerCardViewModel);
        e.Handled = true;
    }

    /// <summary>从启动页的热门推荐卡片打开详情：切到服务器列表并弹出详情浮层。</summary>
    public void ShowServerDetail(ServerEntry entry)
    {
        var card = _all.FirstOrDefault(v => string.Equals(v.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                   ?? _all.FirstOrDefault(v => entry.HasAddress &&
                                               string.Equals(v.Address, entry.Address, StringComparison.OrdinalIgnoreCase))
                   ?? new ServerCardViewModel(entry) { IsFavorite = _store.IsFavorite(entry.Id) };

        NavServers.IsChecked = true; // 先切到服务器列表页
        ShowDetail(card);
    }

    private void ShowDetail(ServerCardViewModel? server)
    {
        if (server is null)
        {
            return;
        }

        SelectedServer = server;
        if (server.Ad is not null)
        {
            _session.Ads.Report(server.Ad, "click_detail"); // 点击详情上报（服务端 24 小时去重）
        }

        HeatService.Current.RecordView(server.Id); // 计入热度
        _ = _session.RemoteCatalog.ReportEventAsync("view", server.Id); // 匿名上报（服务端全站榜单）
        RebuildDetailAddresses(server);
        OnPropertyChanged(nameof(History));
        DetailOverlay.Visibility = Visibility.Visible;
        DetailScroll.ScrollToTop(); // 切换到另一个服务器时滚动条回到顶部

        // 列表状态现在只来自服务端（等级 + 人数）；详情页额外本机查一次这台，
        // 把 MOTD / 玩家名单 / 插件这些"详细信息"补齐（一次请求，不影响列表）。
        _ = RefreshDetailStatusAsync(server);
    }

    /// <summary>详情页打开时本机查一次该服务器，用于补齐详细信息（列表不再全量 ping）。</summary>
    private async Task RefreshDetailStatusAsync(ServerCardViewModel server)
    {
        if (!server.Entry.HasAddress)
        {
            return;
        }

        try
        {
            var status = await _statusProvider.QueryAsync(server.Entry);
            if (ReferenceEquals(SelectedServer, server))
            {
                server.Status = status;
                server.NotifyStatusChanged();

                // 详情页这次是**本机**实测 → 直接覆盖本机信号缓存（用户确认：手动测速总是以最新为准）
                var level = StatusLevels.FromLatency(status.IsOnline ? status.LatencyMs : null);
                var measured = new LocalLatencyEntry(
                    server.Id, level, status.IsOnline ? status.LatencyMs : null,
                    server.SignalLevel, DateTimeOffset.Now);
                _latency.Set(measured);
                server.ApplyLocalLatency(measured.Level, measured.Milliseconds, measured.MeasuredAt);
            }
        }
        catch (Exception ex)
        {
            App.LogLine($"详情页查询失败（{server.Id}）：{ex.Message}");
        }
    }

    /// <summary>按当前服务器的线路重建「服务器地址」列表，并标出用户选择的直连线路。</summary>
    private void RebuildDetailAddresses(ServerCardViewModel server)
    {
        DetailAddresses.Clear();
        var preferred = _store.GetPreferredRoute(server.Id);
        var addresses = server.Addresses;
        for (var i = 0; i < addresses.Count; i++)
        {
            var address = addresses[i];
            var isPreferred = preferred is null
                ? i == 0
                : string.Equals(preferred, address.EndPoint, StringComparison.OrdinalIgnoreCase);
            DetailAddresses.Add(new ServerAddressItem(server.Id, address, i == 0, isPreferred));
        }
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServerAddressItem item)
        {
            return;
        }

        try
        {
            Clipboard.SetText(item.EndPoint);
            StatusBar.Text = $"已复制 {item.EndPoint} 到剪贴板。";
            ShowCopyToast($"已复制地址：{item.EndPoint}");
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"复制失败：{ex.Message}";
            ShowCopyToast("复制地址失败");
        }
    }

    /// <summary>一次性并行探测该服务器的所有线路（详情页只有一个 Ping 按钮）。</summary>
    private async void OnPingAllAddresses(object sender, RoutedEventArgs e)
    {
        if (DetailAddresses.Count == 0)
        {
            return;
        }

        foreach (var address in DetailAddresses)
        {
            address.StatusText = "探测中…";
        }

        var resolver = new MinecraftSrvResolver();
        var ping = new ServerListPing();
        await Task.WhenAll(DetailAddresses.Select(async item =>
        {
            try
            {
                var (host, port) = await resolver.ResolveAsync(item.Host, item.Port);
                var status = await ping.QueryAsync(host, port, TimeSpan.FromSeconds(5));
                item.StatusText = status.IsOnline ? $"在线 · {status.LatencyMs} ms" : "未开服";
            }
            catch (Exception)
            {
                item.StatusText = "未开服";
            }
        }));
    }

    private void OnSetPreferredRoute(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServerAddressItem item)
        {
            return;
        }

        foreach (var address in DetailAddresses)
        {
            address.IsPreferred = ReferenceEquals(address, item);
        }

        _store.SetPreferredRoute(item.ServerId, item.EndPoint);
        StatusBar.Text = $"直连启动将使用：{item.EndPoint}";
    }

    /// <summary>直连启动使用的地址：优先用户在该服务器详情页选定的线路。</summary>
    private string ResolveEndpoint(ServerCardViewModel server) =>
        _store.GetPreferredRoute(server.Id) ?? server.EndPoint;

    /// <summary>打开第三方网址前先给安全提示，确认后才用默认浏览器打开。</summary>
    private void OpenExternalLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var confirmed = Views.MessageWindow.Confirm(
            this,
            "安全提示",
            $"该网址为第三方提供：\n{url}\n\n本软件不对其内容负责，是否继续访问？",
            "继续访问",
            "取消");
        if (!confirmed)
        {
            StatusBar.Text = "已取消访问。";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
            StatusBar.Text = $"已在默认浏览器打开：{url}";
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"打开失败：{ex.Message}";
        }
    }

    /// <summary>详情页介绍里点击链接。</summary>
    private void OnIntroLinkClicked(object sender, string url) => OpenExternalLink(url);

    private void OnCloseDetail(object sender, RoutedEventArgs e)
    {
        DetailOverlay.Visibility = Visibility.Collapsed;
        SelectedServer = null;
    }

    /// <summary>
    /// 只有点击详情卡之外的黑色遮罩才关闭浮层。
    /// 用 Preview 事件是因为详情里的 ScrollViewer / 文字会处理普通 MouseUp；若在那里判断，
    /// 内部点击会继续冒泡到遮罩并把详情关掉（用户 2026-10-21 再次反馈）。
    /// </summary>
    private void OnOverlayBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (DetailCard.IsMouseOver)
        {
            return;
        }

        OnCloseDetail(sender, e);
        e.Handled = true;
    }

    // ---------- 详情页大图查看（2026-10-21） ----------

    /// <summary>详情页里的图片：图标 + 截图（没有截图时不提供大图入口）。</summary>
    private List<string> BuildViewerImages()
    {
        var images = new List<string>();
        if (SelectedServer is not { } server || !server.HasScreenshots)
        {
            return images;
        }

        if (server.Icon is not null)
        {
            var icon = server.Entry.IconUrl;
            if (string.IsNullOrWhiteSpace(icon))
            {
                icon = AppInfo.CatalogEndpoint.TrimEnd('/') + "/api/v1/icon/" + Uri.EscapeDataString(server.Id);
            }
            else if (!icon!.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                     !icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                icon = AppInfo.CatalogEndpoint.TrimEnd('/') + "/" + icon.TrimStart('/');
            }

            if (!string.IsNullOrWhiteSpace(icon) &&
                !icon!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(icon);
            }
        }

        images.AddRange(server.ScreenshotUrls.Where(url => !string.IsNullOrWhiteSpace(url)));
        return images.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void OnDetailIconClick(object sender, MouseButtonEventArgs e)
    {
        var images = BuildViewerImages();
        if (images.Count > 0)
        {
            OpenImageViewer(images, 0);
        }

        e.Handled = true;
    }

    private void OnScreenshotClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string url)
        {
            return;
        }

        var images = BuildViewerImages();
        var index = images.FindIndex(item => string.Equals(item, url, StringComparison.OrdinalIgnoreCase));
        if (images.Count > 0)
        {
            OpenImageViewer(images, index < 0 ? 0 : index);
        }

        e.Handled = true;
    }

    private void OpenImageViewer(IReadOnlyList<string> images, int index)
    {
        _viewerImages.Clear();
        _viewerImages.AddRange(images
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (_viewerImages.Count == 0)
        {
            return;
        }

        _viewerIndex = Math.Max(0, Math.Min(index, _viewerImages.Count - 1));
        ViewerPrev.Visibility = ViewerNext.Visibility =
            _viewerImages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ImageViewerOverlay.Visibility = Visibility.Visible;
        LoadViewerImage();
    }

    private void LoadViewerImage()
    {
        if (_viewerImages.Count == 0)
        {
            CloseImageViewer();
            return;
        }

        var url = _viewerImages[_viewerIndex];
        ViewerCounter.Text = $"{_viewerIndex + 1} / {_viewerImages.Count}";
        ViewerError.Visibility = Visibility.Collapsed;
        ViewerLoading.Visibility = Visibility.Visible;
        ViewerImage.Visibility = Visibility.Collapsed;
        ViewerImage.Source = null;
        _viewerBitmap = null;

        BitmapImage bitmap;
        try
        {
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnDemand;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DownloadCompleted += (_, _) => Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded, new Action(() => OnViewerImageLoaded(bitmap)));
            bitmap.DownloadFailed += (_, _) => Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded, new Action(OnViewerImageFailed));
            bitmap.EndInit();
        }
        catch (Exception)
        {
            OnViewerImageFailed();
        }
    }

    private void OnViewerImageLoaded(BitmapImage bitmap)
    {
        if (ImageViewerOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        _viewerBitmap = bitmap;
        ViewerImage.Source = bitmap;
        ViewerImage.Width = bitmap.PixelWidth;
        ViewerImage.Height = bitmap.PixelHeight;
        ViewerImage.Visibility = Visibility.Visible;
        ViewerLoading.Visibility = Visibility.Collapsed;
        ViewerError.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitViewerImage));
    }

    private void OnViewerImageFailed()
    {
        _viewerBitmap = null;
        ViewerImage.Source = null;
        ViewerImage.Visibility = Visibility.Collapsed;
        ViewerLoading.Visibility = Visibility.Collapsed;
        ViewerError.Visibility = Visibility.Visible;
    }

    private void FitViewerImage()
    {
        if (_viewerBitmap is null)
        {
            return;
        }

        var viewportWidth = ImageViewerCanvas.ActualWidth;
        var viewportHeight = ImageViewerCanvas.ActualHeight;
        if (viewportWidth < 2 || viewportHeight < 2)
        {
            viewportWidth = ActualWidth;
            viewportHeight = ActualHeight;
        }
        if (viewportWidth < 2 || viewportHeight < 2)
        {
            return;
        }

        const double padding = 56;
        var fit = Math.Min(
            (viewportWidth - padding * 2) / _viewerBitmap.PixelWidth,
            (viewportHeight - padding * 2) / _viewerBitmap.PixelHeight);
        // 小图标不强行放大；大图缩小到适应窗口
        _viewerFitScale = Math.Max(0.01, Math.Min(1.0, fit));
        _viewerScale = _viewerFitScale;
        _viewerOffsetX = (viewportWidth - _viewerBitmap.PixelWidth * _viewerScale) / 2;
        _viewerOffsetY = (viewportHeight - _viewerBitmap.PixelHeight * _viewerScale) / 2;
        ClampViewerOffset();
        ApplyViewerTransform();
    }

    private void ClampViewerOffset()
    {
        if (_viewerBitmap is null)
        {
            return;
        }

        var viewportWidth = ImageViewerCanvas.ActualWidth;
        var viewportHeight = ImageViewerCanvas.ActualHeight;
        var scaledWidth = _viewerBitmap.PixelWidth * _viewerScale;
        var scaledHeight = _viewerBitmap.PixelHeight * _viewerScale;

        _viewerOffsetX = scaledWidth <= viewportWidth
            ? (viewportWidth - scaledWidth) / 2
            : Math.Min(0, Math.Max(viewportWidth - scaledWidth, _viewerOffsetX));
        _viewerOffsetY = scaledHeight <= viewportHeight
            ? (viewportHeight - scaledHeight) / 2
            : Math.Min(0, Math.Max(viewportHeight - scaledHeight, _viewerOffsetY));
    }

    private void ApplyViewerTransform()
    {
        ViewerScale.ScaleX = _viewerScale;
        ViewerScale.ScaleY = _viewerScale;
        ViewerTranslate.X = _viewerOffsetX;
        ViewerTranslate.Y = _viewerOffsetY;
    }

    private void OnImageViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewerBitmap is null || ImageViewerOverlay.Visibility != Visibility.Visible)
        {
            return;
        }

        var point = e.GetPosition(ImageViewerCanvas);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var maxScale = Math.Max(_viewerFitScale, _viewerFitScale * 5);
        var newScale = Math.Max(_viewerFitScale, Math.Min(maxScale, _viewerScale * factor));
        if (Math.Abs(newScale - _viewerScale) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        var ratio = newScale / _viewerScale;
        _viewerOffsetX = point.X - (point.X - _viewerOffsetX) * ratio;
        _viewerOffsetY = point.Y - (point.Y - _viewerOffsetY) * ratio;
        _viewerScale = newScale;
        ClampViewerOffset();
        ApplyViewerTransform();
        e.Handled = true;
    }

    private void OnViewerImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FitViewerImage();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || _viewerBitmap is null)
        {
            return;
        }

        _viewerDragging = true;
        _viewerDragStart = e.GetPosition(ImageViewerCanvas);
        _viewerDragStartX = _viewerOffsetX;
        _viewerDragStartY = _viewerOffsetY;
        ViewerImage.CaptureMouse();
        e.Handled = true;
    }

    private void OnViewerImageMouseMove(object sender, MouseEventArgs e)
    {
        if (!_viewerDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(ImageViewerCanvas);
        _viewerOffsetX = _viewerDragStartX + (point.X - _viewerDragStart.X);
        _viewerOffsetY = _viewerDragStartY + (point.Y - _viewerDragStart.Y);
        ClampViewerOffset();
        ApplyViewerTransform();
        e.Handled = true;
    }

    private void OnViewerImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_viewerDragging)
        {
            return;
        }

        _viewerDragging = false;
        ViewerImage.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnImageViewerBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, ImageViewerOverlay) ||
            ReferenceEquals(e.OriginalSource, ImageViewerCanvas))
        {
            CloseImageViewer();
            e.Handled = true;
        }
    }

    private void OnCloseImageViewer(object sender, RoutedEventArgs e) => CloseImageViewer();

    private void CloseImageViewer()
    {
        _viewerDragging = false;
        ViewerImage.ReleaseMouseCapture();
        ViewerImage.Source = null;
        _viewerBitmap = null;
        _viewerImages.Clear();
        ViewerLoading.Visibility = Visibility.Collapsed;
        ViewerError.Visibility = Visibility.Collapsed;
        ImageViewerOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnViewerPrev(object sender, RoutedEventArgs e) => ChangeViewerImage(-1);

    private void OnViewerNext(object sender, RoutedEventArgs e) => ChangeViewerImage(1);

    private void ChangeViewerImage(int delta)
    {
        if (_viewerImages.Count <= 1)
        {
            return;
        }

        _viewerIndex = (_viewerIndex + delta + _viewerImages.Count) % _viewerImages.Count;
        LoadViewerImage();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ImageViewerOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseImageViewer();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Left)
            {
                ChangeViewerImage(-1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Right)
            {
                ChangeViewerImage(1);
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Escape && DetailOverlay.Visibility == Visibility.Visible)
        {
            OnCloseDetail(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 详情页「从我的列表移除」：只对"已下架但保留"的条目有意义。
    /// 同时取消收藏并清掉游玩历史，避免它下次又被保留回来。
    /// </summary>
    private void OnRemoveRetained(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not { IsRetained: true } server)
        {
            return;
        }

        _session.Catalog.DropRetained(server.Id);
        _store.Forget(server.Id);

        DetailOverlay.Visibility = Visibility.Collapsed;
        SelectedServer = null;
        LoadServers();
        StatusBar.Text = $"已从我的列表移除「{server.Name}」（也取消了收藏与游玩记录）。";
    }

    private void OnToggleFavorite(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServerCardViewModel server)
        {
            return;
        }

        server.IsFavorite = _store.ToggleFavorite(server.Id);
        StatusBar.Text = server.IsFavorite ? $"已收藏 {server.Name}。" : $"已取消收藏 {server.Name}。";
        if (server.IsFavorite)
        {
            _ = _session.RemoteCatalog.ReportEventAsync("favorite", server.Id); // 匿名上报（服务端全站榜单）
        }

        ApplyFilters();
    }

    private void OnCopyIp(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ServerCardViewModel server)
        {
            CopyToClipboard(server);
        }
    }

    private void OnCopyIpSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not null)
        {
            CopyToClipboard(SelectedServer);
        }
    }

    private void CopyToClipboard(ServerCardViewModel server)
    {
        try
        {
            Clipboard.SetText(server.EndPoint);
            StatusBar.Text = $"已复制 {server.EndPoint} 到剪贴板。";
            ShowCopyToast($"已复制 IP：{server.EndPoint}");
            if (server.Ad is not null)
            {
                _session.Ads.Report(server.Ad, "click_copy_ip");
            }
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"复制失败：{ex.Message}";
            ShowCopyToast("复制 IP 失败");
        }
    }

    /// <summary>点击服务器名称直接复制（列表卡片和详情页共用）。</summary>
    private void OnCopyServerName(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServerCardViewModel server ||
            string.IsNullOrWhiteSpace(server.Name))
        {
            return;
        }

        e.Handled = true;
        try
        {
            Clipboard.SetText(server.Name);
            StatusBar.Text = $"已复制服务器名称：{server.Name}";
            ShowCopyToast($"已复制服务器名称：{server.Name}");
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"复制失败：{ex.Message}";
            ShowCopyToast("复制服务器名称失败");
        }
    }

    private async void ShowCopyToast(string message)
    {
        var cancellation = new CancellationTokenSource();
        _copyToastCancellation?.Cancel();
        _copyToastCancellation = cancellation;

        CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
        CopyToastText.Text = message;
        CopyToast.Opacity = 1;
        CopyToast.Visibility = Visibility.Visible;

        try
        {
            // 1 秒完整显示 + 1 秒渐隐，共 2 秒。
            await Task.Delay(1000, cancellation.Token);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(1))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            CopyToast.BeginAnimation(UIElement.OpacityProperty, fade);
            await Task.Delay(1000, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // 新提示已接管；不要隐藏或改写它。
        }
        finally
        {
            if (ReferenceEquals(_copyToastCancellation, cancellation))
            {
                CopyToast.BeginAnimation(UIElement.OpacityProperty, null);
                CopyToast.Opacity = 0;
                CopyToast.Visibility = Visibility.Collapsed;
                _copyToastCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async void OnLaunchServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ServerCardViewModel server)
        {
            await LaunchFromServerAsync(server);
        }
    }

    private async void OnLaunchServerSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not null)
        {
            await LaunchFromServerAsync(SelectedServer);
        }
    }

    // ---------- 收录信息里的「官网 / QQ 群 / 专属客户端」 ----------

    private void OnOpenOfficialSite(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ServerCardViewModel server)
        {
            OpenUrl(server: server);
        }
    }

    private void OnOpenOfficialSiteSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is not null)
        {
            OpenUrl(server: SelectedServer);
        }
    }

    /// <summary>打开某一条专属客户端下载链接（多条链接时每行一个「打开」）。</summary>
    private void OnOpenClientDownloadItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClientDownload download)
        {
            OpenExternalLink(download.Url);
            if (!string.IsNullOrWhiteSpace(download.ExtractCode))
            {
                try
                {
                    System.Windows.Clipboard.SetText(download.ExtractCode!);
                    StatusBar.Text = $"已在浏览器打开下载页，提取码 {download.ExtractCode} 已复制到剪贴板。";
                }
                catch (Exception)
                {
                    StatusBar.Text = $"已在浏览器打开下载页，提取码：{download.ExtractCode}";
                }
            }
        }
    }

    private void OnOpenClientDownload(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is null)
        {
            return;
        }

        // 客户端链接优先，其次官网，最后来源页（都能让用户找到下载方式）
        var url = SelectedServer.Entry.ClientDownloadUrl
                  ?? SelectedServer.Entry.OfficialSite
                  ?? SelectedServer.Entry.SourceUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusBar.Text = "该服务器没有提供客户端下载地址，请通过 QQ 群或官网联系服主。";
            return;
        }

        OpenUrl(url: url);
    }

    private void OnCopyQqGroup(object sender, RoutedEventArgs e)
    {
        if (SelectedServer is null || string.IsNullOrWhiteSpace(SelectedServer.Entry.QqGroup))
        {
            return;
        }

        try
        {
            Clipboard.SetText(SelectedServer.Entry.QqGroup!);
            StatusBar.Text = $"已复制 QQ 群号：{SelectedServer.Entry.QqGroup}";
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"复制失败：{ex.Message}";
        }
    }

    private void OpenUrl(ServerCardViewModel? server = null, string? url = null)
    {
        url ??= server?.Entry.OfficialSite ?? server?.Entry.SourceUrl;
        OpenExternalLink(url ?? string.Empty);
    }

    /// <summary>从服务器列表一键启动：填入服务器地址，切到启动器页并尝试启动。</summary>
    private async Task LaunchFromServerAsync(ServerCardViewModel server)
    {
        if (server.Ad is not null)
        {
            _session.Ads.Report(server.Ad, "click_join"); // 进服点击上报（需求事件类型 click_join）
        }

        var endpoint = ResolveEndpoint(server); // 多线路时用用户在详情页选定的线路
        Launcher.PrefillServer(endpoint);
        NavLaunch.IsChecked = true;

        if (!_session.IsReadyToLaunch)
        {
            StatusBar.Text = $"已填入 {server.Name}（{endpoint}）。请先在启动器页完成账户登录与 Java 选择。";
            return;
        }

        // 启动前处理版本：匹配直接启动；不匹配先自动切换（严格档）或让用户挑（跨版本）；
        // 本机没有匹配版本时弹「下载并启动」，装完自动切过去。
        if (!await EnsureCompatibleVersionAsync(server.Entry, server.Name))
        {
            return;
        }

        var confirmed = Views.MessageWindow.Confirm(
            this,
            "启动游戏",
            $"将启动游戏并进入：\n\n{server.Name}\n{endpoint}\n\n是否继续？",
            "启动",
            "取消");
        if (!confirmed)
        {
            return;
        }

        StatusBar.Text = $"正在启动 {server.Name}…";
        await Launcher.LaunchWithCurrentSettingsAsync();
        // 游玩历史由启动器在启动成功后按"直连地址反查条目"统一记录（见 LauncherPage.RecordLaunchHistory），
        // 这里不再重复写，否则「最近玩过的服务器」会出现两条同样的记录。
        HeatService.Current.RecordLaunch(server.Id); // 直连启动加热度
        _ = _session.RemoteCatalog.ReportEventAsync("launch", server.Id); // 匿名上报（服务端全站榜单）
        OnPropertyChanged(nameof(History));
        StatusBar.Text = $"已向启动器发出启动指令：{server.Name}（{endpoint}）。";
    }

    /// <summary>
    /// 启动前校验：即将启动的游戏版本是否在服务器支持范围内（跨版本服务器按区间判断）。
    /// 返回 false 表示用户选择放弃启动。
    /// </summary>
    public bool CheckVersionCompatibility(ServerEntry entry, string serverName)
    {
        var gameVersion = _session.CurrentVersionId;
        if (Core.Models.VersionCompatibility.IsCompatible(entry, gameVersion))
        {
            return true;
        }

        var supported = Core.Models.VersionCompatibility.DescribeSupported(entry);
        return Views.MessageWindow.Confirm(
            this,
            "版本可能不匹配",
            $"服务器「{serverName}」支持的版本是 {supported}，\n而当前要启动的是 {gameVersion}。\n\n" +
            "直接进入可能被服务器拒绝或出现兼容问题，是否仍然启动？",
            "仍然启动",
            "取消");
    }

    /// <summary>
    /// 直连启动前的版本处理（2026-10-06 用户确认的规则）：
    /// 版本匹配 → 直接放行；不匹配 → 不支持跨版本的服务器按<b>严格版本档</b>自动切换，
    /// 支持跨版本的服务器弹窗让用户挑；本机没有匹配版本 → 弹「下载并启动」，
    /// 装完（原版 → 加载器 → 光影）自动切换版本。返回 false 表示用户取消或安装失败。
    /// </summary>
    public async Task<bool> EnsureCompatibleVersionAsync(ServerEntry entry, string serverName)
    {
        var current = _session.CurrentVersionId;
        if (Core.Models.VersionCompatibility.IsCompatible(entry, current))
        {
            return true;
        }

        var supported = Core.Models.VersionCompatibility.DescribeSupported(entry);
        var installed = _session.Installation.GetInstalledVersions(_session.GameRoot)
            .Where(v => v.IsUsable)
            .Select(v => v.Id)
            .ToList();

        string? chosen;
        if (Core.Models.VersionSelection.SupportsCrossVersion(entry))
        {
            // 支持跨版本 → 不替用户决定，弹窗让他挑
            var candidates = Core.Models.VersionSelection.PickCompatibleCandidates(entry, installed);
            if (candidates.Count == 0)
            {
                chosen = null;
            }
            else
            {
                var picker = new Views.VersionPickerWindow(this, serverName, supported, candidates);
                if (picker.ShowDialog() != true)
                {
                    StatusBar.Text = $"已取消启动（未选择版本）：{serverName}";
                    return false;
                }

                chosen = picker.SelectedVersionId;
            }
        }
        else
        {
            // 不支持跨版本 → 严格按服务器声明的版本档
            chosen = Core.Models.VersionSelection.PickStrictCandidates(entry, installed).FirstOrDefault();
        }

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            _session.CurrentVersionId = chosen;
            StatusBar.Text = $"已自动切换到 {chosen}（服务器「{serverName}」支持 {supported}）。";
            return true;
        }

        return await PromptAndInstallVersionAsync(entry, serverName, supported);
    }

    /// <summary>本机没有匹配版本：弹预填好的下载确认窗，装完自动切换并放行启动。</summary>
    private async Task<bool> PromptAndInstallVersionAsync(ServerEntry entry, string serverName, string supported)
    {
        if (!AppEdition.AllowsVersionDownload)
        {
            Views.MessageWindow.Info(
                this,
                "本机没有匹配版本",
                $"服务器「{serverName}」支持 {supported}，但本机还没有可用的匹配版本。\n\n" +
                "WorldBirdLauncher 不提供游戏版本下载：请先用官方启动器安装对应版本，" +
                "或导入一个自带游戏本体的离线整合包，然后再启动。");
            StatusBar.Text = $"已取消启动（本机没有 {supported} 的可用版本）：{serverName}";
            return false;
        }

        var band = Core.Models.VersionSelection.TargetBand(entry);

        IReadOnlyList<string> releases;
        try
        {
            var remote = await _session.Installation.GetRemoteVersionsAsync();
            releases = remote
                .Where(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            Views.MessageWindow.Info(this, "无法获取版本清单",
                $"读取远程版本列表失败：{ex.Message}\n\n请稍后重试，或到「资源下载 → 安装游戏版本」手动安装。");
            return false;
        }

        var sameBand = releases
            .Where(id => Core.Models.VersionSelection.IsSameBand(band, id))
            .OrderByDescending(id => id, Comparer<string>.Create(Core.Models.VersionSelection.CompareVersions))
            .ToList();
        var recommended = sameBand.FirstOrDefault()
                          ?? Core.Models.VersionSelection.PickRecommendedRemoteVersion(band, releases);
        if (string.IsNullOrWhiteSpace(recommended))
        {
            Views.MessageWindow.Info(this, "没有可下载的版本",
                $"服务器「{serverName}」声明支持 {supported}，但没有在官方版本清单里找到对应版本。\n\n" +
                "可以到「资源下载 → 安装游戏版本」手动安装。");
            return false;
        }

        var (defaultLoader, defaultShader, note) = SuggestLoaderAndShader(entry);
        var window = new Views.AutoInstallWindow(
            this, serverName, supported, sameBand, recommended!, defaultLoader, defaultShader, note);
        if (window.ShowDialog() != true || window.Plan is null)
        {
            StatusBar.Text = $"已取消下载：{serverName}";
            return false;
        }

        var plan = window.Plan;
        var loaderText = plan.Loader == VersionInstallPlan.NoLoader ? "原版" : $"{plan.Loader} {plan.LoaderVersion}";
        var shaderText = plan.Shader == VersionInstallPlan.NoShader ? "无光影" : plan.Shader;
        var steps = new List<DownloadStep>
        {
            new(DownloadCategory.游戏本体, $"游戏本体 {plan.GameVersion}"),
        };
        if (plan.Loader != VersionInstallPlan.NoLoader)
        {
            steps.Add(new DownloadStep(DownloadCategory.模组加载器, $"{plan.Loader} {plan.LoaderVersion}"));
        }

        if (plan.Shader != VersionInstallPlan.NoShader)
        {
            steps.Add(new DownloadStep(DownloadCategory.光影, plan.Shader));
        }

        var task = new DownloadTask(
            $"{plan.GameVersion}（{loaderText} · {shaderText}）→ {plan.TargetName}（直连启动）", steps);
        DownloadManager.Current.Enqueue(task, async (progress, token) =>
        {
            var result = await VersionInstallService.InstallAsync(plan, steps, progress, token);
            return (result.Success, result.Message);
        });

        StatusBar.Text = $"正在下载 {task.Title}…装完会自动切换版本并启动。";
        var success = await task.Completion;
        if (!success)
        {
            Views.MessageWindow.Info(this, "下载失败", $"{task.Message}\n\n可在「资源下载」页查看详情或重试。");
            StatusBar.Text = $"下载失败：{serverName}";
            return false;
        }

        StatusBar.Text = $"{task.Message}（来自直连启动）";
        return true;
    }

    /// <summary>按服务器声明的加载器给出默认选择与注意事项。</summary>
    private static (string Loader, string Shader, string? Note) SuggestLoaderAndShader(ServerEntry entry)
    {
        var loader = entry.Loader?.Trim() switch
        {
            "Fabric" => "Fabric",
            "Forge" => "Forge",
            _ => VersionInstallPlan.NoLoader, // 原版 / NeoForge（暂不支持自动装）/ 未知
        };

        var shader = loader is "Fabric" ? "Iris" : "OptiFine";
        var notes = new List<string>();
        if (string.Equals(entry.Loader?.Trim(), "NeoForge", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("本启动器暂不支持自动安装 NeoForge，这里先按原版 + 光影装；需要 NeoForge 请手动安装后再启动");
        }

        if (loader == "Forge")
        {
            notes.Add("Forge 服务器常要求特定 Forge 版本，自动装的是官方最新版，进不去请在「安装版本」里手动挑版本");
        }

        return (loader, shader, notes.Count == 0 ? null : string.Join("；", notes));
    }

    private void OnSubmitServer(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppInfo.SubmitIssueUrl())
            {
                UseShellExecute = true,
            });
            StatusBar.Text = "已打开服务器提交页面，填写后提交即可（首次提交需人工审核）。";
        }
        catch (Exception ex)
        {
            StatusBar.Text = $"无法打开浏览器：{ex.Message}";
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}
