using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Launcher;
using WorldBirdLauncher.Core.Networking;
using WorldBirdLauncher.Core.Protocol;
using WorldBirdLauncher.Core.Services;

namespace WorldBirdLauncher.App.Views;

public partial class LauncherPage : UserControl
{
    private readonly AppSession _session = AppSession.Current;
    private readonly SkinService _skins = new();
    private readonly ServerStatusProvider _statusProvider = new(enableQuery: false);
    private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private static readonly MinecraftSrvResolver SrvResolver = new();
    private static readonly ServerListPing ServerPing = new();
    private bool _busy;
    private Button? _busyButton;
    private bool _initialized;

    public LauncherPage()
    {
        InitializeComponent();
        // 账户在别处（如皮肤页/登录窗口）被切换时，头像与身份信息自动跟随
        _session.AccountChanged += (_, _) => Dispatcher.InvokeAsync(RefreshIdentity);
        // ★ 首次启动的 BUG：首启时本地还没目录，LoadHotServers() 跑在空列表上 → "热门服务器"是空的。
        //   目录到位后自动重新算一次热门（不用手动刷新）。
        _session.RemoteCatalog.UpdateReady += update => Dispatcher.InvokeAsync(() =>
        {
            LoadHotServers();
            _ = LoadRecentServersAsync();
        });

        Loaded += (_, _) =>
        {
            if (!_initialized)
            {
                _initialized = true;
                TipText.Text = _session.Tips.Next();
                _tipTimer.Tick += (_, _) => TipText.Text = _session.Tips.Next();
                _tipTimer.Start();
            }

            RefreshIdentity();
            _session.EnsureJavaRestored(); // 按上次选择恢复 Java（找不到就自动挑）
            ReloadFromSettings();
            LoadHotServers();
            _ = LoadRecentServersAsync();
        };
    }

    /// <summary>设置页保存后调用，刷新摘要与推荐。</summary>
    /// <summary>游戏退出、窗口重新显示时把进度条与提示文字重置，避免残留上一次的启动状态。</summary>
    public void ResetLaunchStatus()
    {
        Progress.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        SetHint(null);
    }

    public void ReloadFromSettings()
    {
        var summaryVersionId = _session.CurrentVersionId ?? string.Empty;
        var required = JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, summaryVersionId);
        var allowsNewer = JavaRuntimeService.AllowsNewerJava(summaryVersionId);
        var javaText = _session.CurrentJava?.DisplayName ?? "未选择";
        if (_session.CurrentJava is { } java)
        {
            if (!allowsNewer && java.MajorVersion != 8)
            {
                javaText += "（老版本必须 Java 8）";
            }
            else if (allowsNewer && java.MajorVersion < required)
            {
                javaText += $"（本版本需要 Java {required}）";
            }
        }

        LaunchSummaryText.Text =
            $"版本：{_session.CurrentVersionId ?? "未选择"}　" +
            $"Java：{javaText}　" +
            $"内存：{_session.Settings.MaxMemoryMb} MB　" +
            $"版本隔离：{(_session.Settings.VersionIsolation ? "开" : "关")}";

        var server = _session.Settings.DefaultServerAddress;
        ServerHintText.Text = string.IsNullOrWhiteSpace(server)
            ? "未设置默认服务器（可在设置页填写，或点右侧推荐服务器的「启动」）"
            : $"启动后会进入：{server}";
    }

    /// <summary>
    /// 「启动游戏」下方的**提示/进度文字**（"已选中 xxx…"、"正在下载…"、"游戏已启动…"）按用户要求
    /// 合并到**程序最底部的提示行**显示，由主窗口订阅本事件写进底部提示（启动页按钮下方不再重复显示）。
    /// 注意：版本 / Java / 内存 / 版本隔离 / 直连地址这些**标签**仍留在按钮下方（用户明确要求分开）。
    /// </summary>
    public event Action<string>? HintChanged;

    /// <summary>把提示/进度文字送到最底部提示行；非 UI 线程调用会自动切回 UI 线程。</summary>
    private void SetHint(string? text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => SetHint(text));
            return;
        }

        ProgressText.Text = text ?? string.Empty; // 元素保留但不再显示（底部提示行才是展示位）
        HintChanged?.Invoke(text ?? string.Empty);
    }

    /// <summary>由服务器列表或推荐列表填入服务器地址。</summary>
    public void PrefillServer(string address)
    {
        _session.Settings.DefaultServerAddress = address;
        _session.Settings.Save();
        ReloadFromSettings();
    }

    /// <summary>供服务器列表调用：用当前设置直接启动。</summary>
    public Task LaunchWithCurrentSettingsAsync() => DoLaunchAsync();

    // ---------- 账户 ----------

    private void RefreshIdentity()
    {
        var account = _session.CurrentAccount;
        UserNameText.Text = account?.Name ?? "未登录";
        UserTypeText.Text = account is null
            ? "使用 Microsoft 账户登录"
            : "微软正版账户";

        AvatarText.Text = string.IsNullOrWhiteSpace(account?.Name) ? "?" : account!.Name[..1].ToUpperInvariant();
        _ = RefreshAvatarAsync(account);
    }

    /// <summary>Loads the Microsoft profile skin and uses its head as the account avatar.</summary>
    private async Task RefreshAvatarAsync(AccountProfile? account)
    {
        if (account is null)
        {
            AvatarImage.Source = null;
            return;
        }

        ImageSource? head = null;
        try
        {
            var skin = await _skins.ResolveAsync(account);
            head = skin is null ? null : SkinHead.FromSkin(skin.SkinPng);
        }
        catch (Exception)
        {
            head = null; // 取不到就保留首字母占位
        }

        // ★ SkinService 全程 ConfigureAwait(false)，await 回来后已经在线程池线程上，
        //   这里直接赋 Image.Source 会抛跨线程异常、又被上面的 catch 吞掉 →
        //   表现就是"头像永远只有首字母"（AGENTS.md 第 27 条）。必须切回 UI 线程再赋值。
        await Dispatcher.InvokeAsync(() => AvatarImage.Source = head);
    }

    private bool OpenLogin()
    {
        var dialog = new LoginWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        RefreshIdentity();
        SetStatus($"登录成功：{_session.CurrentAccount?.DisplayText}");
        return true;
    }

    /// <summary>打开账号列表（多账号管理：切换 / 登出 / 删除）。</summary>
    private bool OpenSwitcher()
    {
        var dialog = new AccountSwitcherWindow { Owner = Window.GetWindow(this) };
        var ok = dialog.ShowDialog() == true;
        RefreshIdentity();
        if (ok && dialog.SwitchedTo is { } account)
        {
            SetStatus($"已切换到 {account.DisplayText}");
        }
        else if (!ok)
        {
            SetStatus("已关闭账号列表。");
        }

        return ok;
    }

    private void OnSwitchAccount(object sender, RoutedEventArgs e)
    {
        if (_session.AccountList.Count == 0)
        {
            OpenLogin();
            return;
        }

        OpenSwitcher();
    }

    /// <summary>Launching never opens the authorization window by itself.</summary>
    private Task<bool> EnsureAccountAsync()
    {
        if (_session.CurrentAccount is not null)
        {
            return Task.FromResult(true);
        }

        SetStatus("还没有登录：请点击「Microsoft 账户管理」完成正版授权。");
        return Task.FromResult(false);
    }

    // ---------- 提示与进度 ----------

    private void SetStatus(string text)
    {
        SetHint(text);
    }

    /// <summary>
    /// 游戏 stdout/stderr 的回调是在线程池线程上触发的，直接改 WPF 控件会抛
    /// "调用线程无法访问此对象"（实测会把整个程序打挂，表现为"启动后启动器消失、
    /// 游戏结束也不回来"），所以这里必须切回 UI 线程。
    /// </summary>
    private void SetStatusFromGameLog(string text) =>
        Dispatcher.InvokeAsync(() => SetStatus(text));

    private void Report(InstallProgress p)
    {
        Progress.Visibility = Visibility.Visible;
        if (p.Percentage >= 0)
        {
            Progress.Value = p.Percentage;
        }

        // 带上「当前文件 · 已下/总量 · 速度 · 剩余」（没有就退回原来的 Stage · Detail）
        var metrics = ProgressFormat.Metrics(p);
        var detail = metrics is null
            ? p.Detail
            : p.Detail is null ? metrics : $"{p.Detail} · {metrics}";
        SetHint(detail is null ? p.Stage : $"{p.Stage} · {detail}");
    }

    /// <summary>
    /// 手动换一条提示时重新计时：否则刚换的这条可能 2 秒后又被自动轮换掉，
    /// 达不到"每条提示完整展示 10 秒"的效果。
    /// </summary>
    private void OnNextTip(object sender, RoutedEventArgs e)
    {
        _tipTimer.Stop();
        TipText.Text = _session.Tips.Next();
        _tipTimer.Start();
    }

    // ---------- 推荐服务器 ----------

    private async void LoadHotServers()
    {
        try
        {
            BuildHotCandidates();
            await RefreshHotListAsync();
        }
        catch (Exception)
        {
        }
    }

    private List<Core.Models.ServerEntry> _hotCandidates = [];

    /// <summary>
    /// 热门榜候选：优先按热度（HeatService，含时间衰减）排序，热度不足时用"国内优先 + 名称"补齐，
    /// 保证首启也有内容可展示。
    /// </summary>
    private void BuildHotCandidates()
    {
        var all = _session.Catalog.LoadEntries()
            .Where(e => e.HasAddress) // 推荐位必须能直连
            .ToList();

        var favoriteIds = all.Where(e => _session.Store.IsFavorite(e.Id)).Select(e => e.Id).ToList();

        // 热度：优先用服务端聚合的全站榜单（+ 本机收藏加权），拿不到就退回纯本地热度
        var serverHeat = _session.RemoteCatalog.HeatRanking;
        var heat = new List<string>();
        if (serverHeat.Count > 0)
        {
            var favorites = favoriteIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            heat.AddRange(serverHeat
                .OrderByDescending(x => x.Score + (favorites.Contains(x.Id) ? 2.0 : 0))
                .Take(40)
                .Select(x => x.Id));

            // 本机收藏但服务端还没热度的，也排前面
            heat.AddRange(favoriteIds.Where(id => !heat.Contains(id, StringComparer.OrdinalIgnoreCase)));
        }
        else
        {
            heat.AddRange(HeatService.Current.Ranking(favoriteIds, top: 40).Select(x => x.Id));
        }

        var ordered = new List<Core.Models.ServerEntry>();
        foreach (var id in heat)
        {
            var entry = all.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry is not null && !ordered.Contains(entry))
            {
                ordered.Add(entry);
            }
        }

        ordered.AddRange(all
            .Where(e => !ordered.Contains(e))
            .OrderByDescending(e => e.Region == Core.Models.ServerCategories.RegionDomestic)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase));

        _hotCandidates = ordered;
    }

    private void OnHotFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || HotServerList is null)
        {
            return;
        }

        _ = RefreshHotListAsync();
    }

    private void OnHotTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || HotServerList is null)
        {
            return;
        }

        _ = RefreshHotListAsync();
    }

    private async Task RefreshHotListAsync()
    {
        var query = _hotCandidates.AsEnumerable();

        // 地区：默认只看国内，「显示国外服务器」勾上后国内外都显示
        if (HotShowOverseas.IsChecked != true)
        {
            query = query.Where(e => e.Region == Core.Models.ServerCategories.RegionDomestic);
        }

        // 玩法：标签页
        query = HotCategoryTabs.SelectedIndex switch
        {
            1 => query.Where(e => e.Loader == Core.Models.ServerCategories.LoaderVanilla),
            2 => query.Where(e => e.Categories.Contains(Core.Models.ServerCategories.Modded) ||
                                  e.Loader != Core.Models.ServerCategories.LoaderVanilla),
            3 => query.Where(e => e.Categories.Contains(Core.Models.ServerCategories.MiniGame)),
            4 => query.Where(e => e.Categories.Contains(Core.Models.ServerCategories.Rpg)),
            5 => query.Where(e => e.Categories.Contains(Core.Models.ServerCategories.Survival)),
            _ => query,
        };

        var items = query.Take(10).Select((e, i) => new HotServerItem(e, i + 1)).ToList();

        HotServerList.ItemsSource = items;

        // 在线取服务器图标（Ping 返回的 favicon）
        foreach (var item in items)
        {
            try
            {
                var status = await _statusProvider.QueryAsync(item.Entry);
                item.ApplyIcon(status);
            }
            catch (Exception)
            {
                // 单个服务器失败不影响其他卡片
            }
        }
    }

    private async void OnLaunchHotServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HotServerItem item || _busy)
        {
            return;
        }

        _busyButton = sender as Button;
        // 启动前校验版本匹配（跨版本服务器按区间判断）
        if (Window.GetWindow(this) is MainWindow main && !await main.EnsureCompatibleVersionAsync(item.Entry, item.Name))
        {
            _busyButton = null;
            return;
        }

        var preferred = _session.Store.GetPreferredRoute(item.Entry.Id) ?? item.Entry.EndPoint;
        PrefillServer(preferred);
        HeatService.Current.RecordLaunch(item.Entry.Id); // 从推荐位直连也计入热度
        _ = _session.RemoteCatalog.ReportEventAsync("launch", item.Entry.Id); // 匿名上报（服务端全站榜单）
        await DoLaunchAsync();
    }

    /// <summary>点击热门服务器卡片 → 切到服务器列表并打开它的详情页。</summary>
    private void OnHotServerCardClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HotServerItem item)
        {
            return;
        }

        if (Window.GetWindow(this) is MainWindow main)
        {
            main.ShowServerDetail(item.Entry);
        }
    }

    // ---------- 启动 ----------

    // ---------- 最近玩过的服务器 ----------

    /// <summary>最近游玩卡片最多显示几个（用户要求 6 个）。</summary>
    private const int MaxRecentServers = 6;

    /// <summary>当前被选中（= 直连目标）的服务器 id；null 表示没选。</summary>
    private string? _selectedRecentId;

    /// <summary>本次运行是否已经做过"恢复上次勾选"（只在首次建列表时做一次）。</summary>
    private bool _recentSelectionRestored;

    /// <summary>
    /// 重建「最近玩过的服务器」：取游玩历史（最近的在前）→ 反查目录里的条目 → 取信号等级与图标。
    /// 历史里对不上目录的记录（例如早期版本留下的旧格式）会被自动跳过。
    /// </summary>
    private async Task LoadRecentServersAsync()
    {
        try
        {
            var entries = _session.Catalog.LoadEntries()
                .Where(e => e.HasAddress)
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            RestoreRecentSelection(entries);

            var picks = new List<Core.Models.ServerEntry>();
            foreach (var record in _session.Store.History.OrderByDescending(h => h.Time))
            {
                if (!entries.TryGetValue(record.ServerId ?? string.Empty, out var entry))
                {
                    continue;
                }

                if (picks.Any(e => string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                picks.Add(entry);
                if (picks.Count >= MaxRecentServers)
                {
                    break;
                }
            }

            // 信号等级：服务端采样下发（拿不到就按"待采样"）
            var levels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (picks.Count > 0)
            {
                try
                {
                    foreach (var status in await _session.RemoteCatalog.GetStatusesAsync(picks.Select(e => e.Id)))
                    {
                        levels[status.Id] = status.Level;
                    }
                }
                catch (Exception)
                {
                    // 服务端不可用时保持"待采样"，不影响卡片显示
                }
            }

            // 图标：内存 / 磁盘缓存优先，离线时用上次缓存（和服务器列表一致）
            var icons = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in picks)
            {
                try
                {
                    var bytes = await _session.Icons.GetAsync(entry.Id);
                    icons[entry.Id] = bytes is null || bytes.Length == 0 ? null : DecodePng(bytes);
                }
                catch (Exception)
                {
                    icons[entry.Id] = null;
                }
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var items = picks.Select(entry => new RecentServerItem(entry)
                {
                    SignalLevel = levels.TryGetValue(entry.Id, out var level) ? level : Core.Models.StatusLevels.Unknown,
                    Icon = icons.TryGetValue(entry.Id, out var icon) ? icon : null,
                    IsSelected = string.Equals(entry.Id, _selectedRecentId, StringComparison.OrdinalIgnoreCase),
                }).ToList();

                RecentServerList.ItemsSource = items;
                RecentEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                // Hidden（不是 Collapsed）：始终占位，免得按钮出现/消失时把卡片顶高
                RemoveRecentButton.Visibility = _selectedRecentId is null ? Visibility.Hidden : Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            App.LogLine($"加载「最近玩过的服务器」失败：{ex.Message}");
        }
    }

    private static ImageSource? DecodePng(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 本次启动的"直连目标"服务器：只有「最近玩过的服务器」里勾着的、**且当前要进的地址就是它**才算。
    /// 用户若在「服务器列表」另挑一台启动（地址会被改写），勾选就不再代表本次目标，
    /// 账号与版本检查都不会误触发。
    /// </summary>
    private Core.Models.ServerEntry? ResolveRecentLaunchTarget()
    {
        if (_selectedRecentId is not { Length: > 0 } id)
        {
            return null;
        }

        var entry = _session.Catalog.LoadEntries()
            .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        var address = _session.Settings.DefaultServerAddress;
        if (entry is null || string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var (host, port) = GameLaunchService.SplitServerAddress(address!.Trim());
        return entry.AllAddresses.Any(line =>
            string.Equals(line.Host, host, StringComparison.OrdinalIgnoreCase) && line.Port == port)
            ? entry
            : null;
    }

    private void RestoreRecentSelection(IReadOnlyDictionary<string, Core.Models.ServerEntry> entries)
    {
        if (_recentSelectionRestored)
        {
            return;
        }

        _recentSelectionRestored = true;
        var last = _session.Settings.LastRecentServerId;
        if (string.IsNullOrWhiteSpace(last))
        {
            return;
        }

        if (entries.TryGetValue(last!, out var entry))
        {
            // 上次勾选的这台还在：自动勾选，并把直连地址与账号重新适配（不弹登录窗）
            _selectedRecentId = entry.Id;
            ApplyDirectConnect(entry, restored: true);
            return;
        }

        // 已下架 / 不在目录里：清掉记录，免得界面"假装"还勾着
        _session.Settings.LastRecentServerId = null;
        _session.Settings.Save();
    }

    private void OnRecentWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || RecentScroll is null)
        {
            return;
        }

        RecentScroll.ScrollToHorizontalOffset(RecentScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>
    /// 点最近游玩卡片的头像 = 选中（语义同「直连」）：把直连地址和账号切成这台服务器需要的方式。
    /// **不会自动启动**——用户看清提示后自己点「启动游戏」。
    /// </summary>
    private void OnRecentAvatarClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentServerItem item)
        {
            return;
        }

        var already = string.Equals(_selectedRecentId, item.Entry.Id, StringComparison.OrdinalIgnoreCase);
        _selectedRecentId = already ? null : item.Entry.Id;
        // 记住勾选：下次启动程序时自动勾上（用户 2026-10-08 要求）
        _session.Settings.LastRecentServerId = _selectedRecentId;
        _session.Settings.Save();

        if (already)
        {
            SetStatus($"已取消选中 {item.Name}。");
        }
        else
        {
            ApplyDirectConnect(item.Entry);
        }

        UpdateRecentSelection();
    }

    /// <summary>
    /// 把某台服务器设成直连目标：写死直连地址，并把账号切成它需要的登录方式。
    /// <paramref name="restored"/> = true 表示是"启动时恢复上次勾选"，文案会说明是恢复来的。
    /// </summary>
    private void ApplyDirectConnect(Core.Models.ServerEntry entry, bool restored = false)
    {
        var endpoint = _session.Store.GetPreferredRoute(entry.Id) ?? entry.EndPoint;
        PrefillServer(endpoint); // 写入「启动后进入的服务器」并刷新摘要

        if (AccountMatcher.RequiresExternalAccount(entry))
        {
            SetStatus($"已选中 {entry.Name}（{endpoint}）。{AccountMatcher.UnsupportedExternalAccountMessage}");
            return;
        }

        SetStatus(restored
            ? $"已恢复上次选中的服务器：{entry.Name}（启动后将进入 {endpoint}）。"
            : $"已选中 {entry.Name}：启动后将进入 {endpoint}。");
    }

    /// <summary>同步卡片的选中标记与「移除所选」按钮的显隐。</summary>
    private void UpdateRecentSelection()
    {
        if (RecentServerList.ItemsSource is IEnumerable<RecentServerItem> items)
        {
            foreach (var item in items)
            {
                item.IsSelected = string.Equals(item.Entry.Id, _selectedRecentId, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 用 Hidden：位置一直留着，按钮出现时卡片高度不变（用户 2026-10-08 反馈"会把卡片顶高"）
        RemoveRecentButton.Visibility = _selectedRecentId is null ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>把选中的那台从「最近玩过」里去掉（只清游玩历史，不动收藏）。</summary>
    private void OnRemoveSelectedRecent(object sender, RoutedEventArgs e)
    {
        if (_selectedRecentId is not { Length: > 0 } id)
        {
            return;
        }

        // 二次确认（破坏性操作，用户要求）
        var name = _session.Catalog.LoadEntries()
            .FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))?.Name
            ?? "所选服务器";
        if (!MessageWindow.Confirm(
                Window.GetWindow(this),
                "移除所选服务器",
                $"确定把「{name}」从「最近玩过的服务器」里移除吗？\n\n" +
                "只会清掉本机的游玩历史，收藏状态不受影响；下次再玩到时还会重新出现。",
                "移除",
                "取消"))
        {
            return;
        }

        _session.Store.RemoveHistory(id);
        _selectedRecentId = null;
        _session.Settings.LastRecentServerId = null;
        _session.Settings.Save();
        SetStatus("已从「最近玩过的服务器」移除所选，收藏状态不受影响。");
        _ = LoadRecentServersAsync();
    }

    private async void OnLaunch(object sender, RoutedEventArgs e) => await DoLaunchAsync();

    /// <summary>启动过程中给按钮一个"启动中…"的可见反馈，并防止重复点击。</summary>
    private void SetLaunchingUi(bool busy)
    {
        LaunchButton.IsEnabled = !busy;
        LaunchButton.Content = busy ? "启动中…" : "启动游戏";

        if (_busyButton is not null)
        {
            _busyButton.IsEnabled = !busy;
            _busyButton.Content = busy ? "启动中…" : "启动";
        }
    }

    private async Task DoLaunchAsync()
    {
        if (_busy)
        {
            return;
        }

        SetLaunchingUi(true);
        try
        {
            await LaunchCoreAsync();
        }
        finally
        {
            SetLaunchingUi(false);
            _busyButton = null;
        }
    }

    /// <summary>真正的启动流程；按钮的"启动中…"禁用态由 <see cref="DoLaunchAsync"/> 统一收尾。</summary>
    private async Task LaunchCoreAsync()
    {
        if (_session.CurrentAccount is null && !await EnsureAccountAsync())
        {
            return;
        }

        // 已登出的账户（多账号管理里点了「登出」）不能直接拿去启动：token 已经清掉了，
        // 进服会被服务器拒绝。这里先让他重新登录一次。
        if (_session.CurrentAccount is { } signedOutCandidate && AccountService.IsSignedOut(signedOutCandidate))
        {
            SetStatus($"账户「{signedOutCandidate.Name}」已登出，请点击「Microsoft 账户管理」重新登录后再启动。");
            return;
        }

        // 微软正版账户：access token 快到期时先用 refresh_token 静默续期（免二次授权）。
        // 审核通过前这一步同样会 403，失败就引导重新登录并给出原因。
        if (_session.CurrentAccount is { } microsoft &&
            microsoft.MicrosoftExpiresAt is { } expiresAt &&
            expiresAt <= DateTimeOffset.Now.AddMinutes(5) &&
            !string.IsNullOrWhiteSpace(microsoft.MicrosoftRefreshToken))
        {
            try
            {
                SetStatus("正在续期微软正版登录…");
                var refreshed = await _session.Accounts.RefreshMicrosoftAsync(microsoft);
                _session.AddAccount(refreshed);
            }
            catch (Exception ex)
            {
                SetStatus($"微软登录已过期，请点击「Microsoft 账户管理」重新登录：{ex.Message}");
                return;
            }
        }

        // 本次启动的"直连目标"：只有勾选了「最近玩过的服务器」、且要进的地址就是它时才算
        var launchTarget = ResolveRecentLaunchTarget();

        if (launchTarget is not null && AccountMatcher.RequiresExternalAccount(launchTarget))
        {
            MessageWindow.Info(
                Window.GetWindow(this),
                "无法启动该服务器",
                $"服务器「{launchTarget.Name}」需要外部账户。\n\n" +
                "当前公开版仅支持 Microsoft 账户，因此不会使用不匹配的账户进入服务器。");
            SetStatus($"已取消启动：{launchTarget.Name} 需要外部账户，当前公开版无法连接。");
            return;
        }

        if (string.IsNullOrWhiteSpace(_session.CurrentVersionId))
        {
            SetStatus("还没有选择游戏版本，请到「设置」里选择。");
            return;
        }

        // 只有勾选了「最近玩过的服务器」时才做版本匹配（用户 2026-10-08 要求）：
        // 勾选＝目标明确，版本不匹配就该按既有规则自动切版本 / 让你挑 / 提示下载；
        // 没勾选就是普通启动，不做任何版本检查（保持原行为）。
        if (launchTarget is not null)
        {
            if (Window.GetWindow(this) is MainWindow mainWindow &&
                !await mainWindow.EnsureCompatibleVersionAsync(launchTarget, launchTarget.Name))
            {
                SetStatus($"已取消启动：{launchTarget.Name} 需要的版本还没准备好。");
                return;
            }

            // 版本可能刚被自动切换过，重新取一次；后面的 Java 校验也跟着走新版本
            if (string.IsNullOrWhiteSpace(_session.CurrentVersionId))
            {
                SetStatus("还没有选择游戏版本，请到「设置」里选择。");
                return;
            }
        }

        // Java 版本校验：以版本 JSON 声明的 javaVersion.majorVersion 为准（26.1 需要 Java 25），
        // 而不是硬编码的"高版本=21"。挑选规则见 JavaRuntimeService.PickBest：
        // 完全一致的优先 → 其次"≥ 要求里最小的"（没 Java 17 但有 21 就直接用 21，不再白下）
        // → 1.13 之前的老版本只认 Java 8。真的一个都用不了才自动下载，否则会撞 JVM 的
        // "无法创建 Java 虚拟机"弹窗。
        var required = JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, _session.CurrentVersionId!);
        var allowsNewer = JavaRuntimeService.AllowsNewerJava(_session.CurrentVersionId!);
        var wantedJava = allowsNewer ? required : 8; // 老版本只能用 Java 8
        if (!IsJavaAcceptable(_session.CurrentJava, allowsNewer, required))
        {
            var better = JavaRuntimeService.PickBest(
                _session.GetJavaRuntimes(forceRefresh: true), _session.CurrentVersionId!, required);
            if (better is not null)
            {
                _session.CurrentJava = better;
                SetStatus($"已为本版本自动选用 {better.DisplayName}（{_session.CurrentVersionId} 需要 Java {required}）。");
            }
            else
            {
                // 快扫没找到 ≠ 本机没有：先深度扫一遍（最多 30 秒），免得为 D:\java 这种藏在自定义目录里的 Java 白下 200MB
                SetStatus(allowsNewer
                    ? $"快扫没找到 Java {required}，正在深度扫描各磁盘（最多 30 秒）…"
                    : "这个版本属于老版本（LWJGL2），只能用 Java 8；正在深度扫描各磁盘（最多 30 秒）…");
                var deep = await Task.Run(() => _session.DeepScanJava(
                    onProgress: message => Dispatcher.Invoke(() => SetStatus(message))));
                var deepMatch = JavaRuntimeService.PickBest(deep, _session.CurrentVersionId!, required);
                if (deepMatch is not null)
                {
                    _session.CurrentJava = deepMatch;
                    SetStatus($"深度扫描找到 {deepMatch.DisplayName}，已为本版本选用（{deepMatch.JavaPath}）。");
                }
                else if (!await EnsureJavaAsync(wantedJava))
                {
                    return;
                }
            }
        }

        /// <summary>当前选中的 Java 能不能跑这个版本：老版本（&lt;1.13）只认 Java 8，其余允许用更高的。</summary>
        static bool IsJavaAcceptable(JavaRuntime? java, bool allowsNewer, int required) =>
            java is not null && (allowsNewer ? java.MajorVersion >= required : java.MajorVersion == 8);

        var memory = _session.Settings.MaxMemoryMb;
        if (!_session.CurrentJava!.Is64Bit && memory > 1024)
        {
            memory = 1024;
            _session.Settings.MaxMemoryMb = memory;
            _session.Settings.Save();
            SetStatus($"检测到 32 位 Java，最大内存已自动限制为 {memory} MB（32 位 JVM 无法分配更大堆）。");
        }

        var availableMb = (int)(NetCompat.AvailableMemoryBytes() / 1024 / 1024);
        if (availableMb > 0 && memory > availableMb)
        {
            memory = Math.Max(1024, availableMb - 1024);
            _session.Settings.MaxMemoryMb = memory;
            _session.Settings.Save();
            SetStatus($"可用内存不足，最大内存已自动调整为 {memory} MB。");
        }

        // 启动前先探一次服务器：连不上就问用户是否仍然启动（进入游戏后也可以手动重试）
        var serverAddress = string.IsNullOrWhiteSpace(_session.Settings.DefaultServerAddress)
            ? null
            : _session.Settings.DefaultServerAddress!.Trim();
        if (serverAddress is not null)
        {
            SetStatus($"正在检查服务器 {serverAddress} …");
            if (!await IsServerReachableAsync(serverAddress))
            {
                var keepGoing = MessageWindow.Confirm(
                    Window.GetWindow(this),
                    "服务器无法连接",
                    $"连不上服务器：{serverAddress}\n可能离线、维护中，或地址填错了。\n\n仍然启动游戏吗？（进入游戏后可以手动重试）",
                    "仍然启动",
                    "取消");
                if (!keepGoing)
                {
                    SetStatus($"已取消启动：{serverAddress} 当前无法连接。");
                    return;
                }
            }
        }

        _busy = true;
        Progress.Value = 0;
        Report(new InstallProgress("正在启动游戏…", 0));

        var result = await _session.Launch.LaunchAsync(
            new GameLaunchRequest
            {
                GameRoot = _session.GameRoot,
                VersionId = _session.CurrentVersionId!,
                JavaPath = _session.CurrentJava!.JavaPath,
                Account = _session.CurrentAccount!,
                MaxMemoryMb = memory,
                IsVersionIsolation = _session.Settings.VersionIsolation,
                ServerAddress = serverAddress,
            },
            SetStatusFromGameLog,
            new Progress<InstallProgress>(Report));

        if (result.Success)
        {
            RecordLaunchHistory();
            Progress.Value = 100;
            SetHint($"游戏已启动（PID {result.ProcessId}）。");

            // 交给主窗口跟踪进程：按设置收进托盘，游戏退出后自动显示
            if (result.Process is not null && Window.GetWindow(this) is MainWindow main)
            {
                var versionId = _session.CurrentVersionId ?? string.Empty;
                var versionDirectory = Path.Combine(_session.GameRoot, "versions", versionId);
                main.TrackGameProcess(result.Process, versionId, versionDirectory);
            }
        }
        else
        {
            Progress.Visibility = Visibility.Collapsed;
            SetStatus($"启动失败：{result.Message}");
        }

        _busy = false;
    }

    /// <summary>
    /// 记录一次"玩过的服务器"：用直连地址反查目录条目，写进游玩历史（最近的排最前，
    /// 「最近玩过的服务器」卡片顺序因此跟着更新）。反查不到就不记，免得塞进无效记录。
    /// </summary>
    private void RecordLaunchHistory()
    {
        var address = _session.Settings.DefaultServerAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var target = address!.Trim();
        var (host, port) = GameLaunchService.SplitServerAddress(target);
        var match = _session.Catalog.LoadEntries().FirstOrDefault(e =>
            e.HasAddress && e.AllAddresses.Any(line =>
                string.Equals(line.Host, host, StringComparison.OrdinalIgnoreCase) && line.Port == port));
        if (match is null)
        {
            return;
        }

        _session.Store.AddHistory(match.Id, match.Name, target);
        _ = LoadRecentServersAsync();
    }

    /// <summary>
    /// 启动前探测服务器是否在线（含 SRV 解析）。任何异常都按"连不上"处理，
    /// 由调用方决定是否提示用户；不会抛出。
    /// </summary>
    private static async Task<bool> IsServerReachableAsync(string address)
    {
        try
        {
            var (host, port) = GameLaunchService.SplitServerAddress(address);
            if (string.IsNullOrWhiteSpace(host))
            {
                return true;
            }

            var (resolvedHost, resolvedPort) = await SrvResolver.ResolveAsync(host, port);
            var status = await ServerPing.QueryAsync(resolvedHost, resolvedPort, TimeSpan.FromSeconds(5));
            return status.IsOnline;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 本机没有满足版本要求的 Java 时自动下载安装（约 200 MB，带进度提示）。
    /// 下载失败会给出可操作的中文提示，而不是让用户去撞 JVM 的英文弹窗。
    /// </summary>
    private async Task<bool> EnsureJavaAsync(int required)
    {
        _busy = true;
        Progress.Value = 0;
        Report(new InstallProgress($"本机没有满足要求的 Java，正在下载 Java {required}…", 0));
        try
        {
            var java = await _session.Java.InstallAsync(required, new Progress<InstallProgress>(Report));
            _session.CurrentJava = java;
            _session.InvalidateJavaCache();
            SetStatus($"已安装 {java.DisplayName}（{java.JavaPath}）。");
            return true;
        }
        catch (Exception ex)
        {
            Progress.Visibility = Visibility.Collapsed;
            SetStatus($"自动下载 Java {required} 失败：{ex.Message}。可在「设置 → 版本与运行时维护」点「下载适配 Java」重试，" +
                      $"或手动安装 Java {required} 后在设置页选择。");
            return false;
        }
        finally
        {
            _busy = false;
        }
    }
}

/// <summary>热门服务器推荐项（含图标）。</summary>
public sealed class HotServerItem : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _icon;

    public HotServerItem(Core.Models.ServerEntry entry, int rank)
    {
        Entry = entry;
        Rank = rank;
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public Core.Models.ServerEntry Entry { get; }

    public int Rank { get; }

    public string RankText => $"TOP{Rank}";

    public string Name => Entry.Name;

    public string Address => Entry.EndPoint;

    public string Version => Entry.Version;

    public string Loader => Entry.Loader;

    public string RegionText => Entry.Region;

    public string CategoryText => string.Join(" · ", Entry.Categories);

    public string Initial => string.IsNullOrWhiteSpace(Entry.Name) ? "?" : Entry.Name[..1].ToUpperInvariant();

    public ImageSource? Icon => _icon;

    public void ApplyIcon(Core.Models.ServerStatus status)
    {
        var data = status.FaviconDataUri;
        if (string.IsNullOrWhiteSpace(data))
        {
            return;
        }

        try
        {
            var comma = data!.IndexOf(',');
            if (comma < 0)
            {
                return;
            }

            var bytes = Convert.FromBase64String(data[(comma + 1)..]);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _icon = image;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// 「最近玩过的服务器」卡片项：按用户要求只显示 图标 / 名称 / 信号等级，
/// 另有"是否被选中"（选中后卡片右下角出现勾选标记）。
/// </summary>
public sealed class RecentServerItem : System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _icon;
    private string _signalLevel = Core.Models.StatusLevels.Unknown;
    private bool _isSelected;

    public RecentServerItem(Core.Models.ServerEntry entry) => Entry = entry;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public Core.Models.ServerEntry Entry { get; }

    public string Name => Entry.Name;

    public string Initial => string.IsNullOrWhiteSpace(Entry.Name) ? "?" : Entry.Name[..1].ToUpperInvariant();

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Raise(nameof(Icon));
        }
    }

    /// <summary>信号等级（good / fair / poor / offline / no-address / unknown）。</summary>
    public string SignalLevel
    {
        get => _signalLevel;
        set
        {
            if (_signalLevel == value)
            {
                return;
            }

            _signalLevel = value;
            Raise(nameof(SignalLevel));
            Raise(nameof(SignalText));
        }
    }

    public string SignalText => Core.Models.StatusLevels.DisplayName(SignalLevel);

    /// <summary>是否为当前选中的直连目标。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise(nameof(IsSelected));
            Raise(nameof(CheckVisibility));
        }
    }

    /// <summary>只有选中的卡片才显示右下角的勾选标记（用户要求）。</summary>
    public Visibility CheckVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
