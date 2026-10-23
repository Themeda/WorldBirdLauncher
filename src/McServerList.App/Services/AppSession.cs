using McServerList.Core.Launcher;
using McServerList.Core.Services;
using McServerList.App.Ads;

namespace McServerList.App.Services;

/// <summary>
/// 应用级共享会话：设置、本地数据与启动器各服务的单一实例，
/// 供「服务器列表」与「启动器」两个页面共用同一份状态。
/// </summary>
public sealed class AppSession
{
    private AppSession()
    {
        Settings = AppSettings.Load();
        Store = LocalStore.Load();
        Accounts = new AccountService();
        Java = new JavaRuntimeService();
        Installation = new GameInstallationService();
        // CurseForge 整合包的模组下载地址由**我们服务端**代理解析（客户端不内置 API Key）
        Modpacks = new ModpackInstaller(
            Installation,
            new ServerCurseForgeResolver(AppInfo.CatalogEndpoint, AppInfo.CatalogClientKey));
        Launch = new GameLaunchService();
        Catalog = new CatalogStore();
        RemoteCatalog = new RemoteCatalogService(Catalog);
        // 服务端把条目下架时：收藏过 / 玩过的留在本地（显示为"离线"，可手动移除）
        RemoteCatalog.ShouldRetainRemoved = id =>
            Store.IsFavorite(id) ||
            Store.History.Any(h => string.Equals(h.ServerId, id, StringComparison.OrdinalIgnoreCase));
        Icons = new IconCache(new CatalogApiClient());
        Tips = new TipsService();
        // 付费推广：广告拉取/缓存/事件上报（用目录缓存里的匿名设备号，不读系统 MachineGuid）
        Ads = new AdService(Catalog.DeviceId);
        // 服务端下发新文案后，启动页的提示列表跟着刷新
        RemoteCatalog.TipsUpdated += Tips.Reload;

        Installation.SetDownloadSource(Settings.UseMirror);
        // 阉割版不提供离线登录：旧版本留下的离线账户不显示、也不自动选中（数据仍留在 accounts.dat 里）
        AccountList = Accounts.Load()
            .Where(a => AppEdition.AllowsOfflineLogin || a.Kind != AccountKind.离线)
            .ToList();

        // 恢复上次使用的账户 / 游戏版本 / Java（找不到才回退到默认值）
        _suppressPersist = true;
        CurrentAccount = RestoreAccount()
                         ?? (AppEdition.AllowsOfflineLogin
                             ? AccountList.FirstOrDefault(a => a.Kind == AccountKind.离线)
                             : null)
                         ?? AccountList.FirstOrDefault();
        CurrentVersionId = Settings.LastVersionId is { Length: > 0 } lastVersion &&
                           System.IO.Directory.Exists(Path.Combine(GameRoot, "versions", lastVersion))
            ? lastVersion
            : null;
        _suppressPersist = false;
    }

    private bool _javaRestoreAttempted;

    /// <summary>
    /// 按上次选择的路径恢复 Java（延迟执行：全盘扫描较重，等界面第一次需要时再做）。
    /// 找不到就保持未选，由启动流程自动挑一个满足要求的。
    /// </summary>
    public void EnsureJavaRestored()
    {
        if (CurrentJava is not null || _javaRestoreAttempted || string.IsNullOrWhiteSpace(Settings.LastJavaPath))
        {
            return;
        }

        _javaRestoreAttempted = true;
        var match = GetJavaRuntimes().FirstOrDefault(j =>
            string.Equals(j.JavaPath, Settings.LastJavaPath, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            CurrentJava = match;
        }
    }

    private AccountProfile? RestoreAccount()
    {
        if (string.IsNullOrWhiteSpace(Settings.LastAccountName) ||
            !Enum.TryParse<AccountKind>(Settings.LastAccountKind, out var kind))
        {
            return null;
        }

        return AccountList.FirstOrDefault(a =>
            a.Kind == kind && string.Equals(a.Name, Settings.LastAccountName, StringComparison.OrdinalIgnoreCase));
    }

    private bool _suppressPersist;

    /// <summary>把「上次用的账户 / 版本 / Java」写回设置文件。</summary>
    private void PersistSelection()
    {
        if (_suppressPersist)
        {
            return;
        }

        Settings.LastAccountKind = CurrentAccount?.Kind.ToString();
        Settings.LastAccountName = CurrentAccount?.Name;
        Settings.LastVersionId = CurrentVersionId;
        Settings.LastJavaPath = CurrentJava?.JavaPath;
        Settings.Save();
    }

    public static AppSession Current { get; } = new();

    public AppSettings Settings { get; }

    public LocalStore Store { get; }

    public AccountService Accounts { get; }

    public JavaRuntimeService Java { get; }

    private IReadOnlyList<JavaRuntime>? _javaCache;

    /// <summary>
    /// 缓存的 Java 列表。默认只做**快扫**（注册表 / JAVA_HOME / PATH / 常见目录 / 官方启动器运行时，
    /// 外加「上次深度扫描的记忆」java-scan.json），通常 0.1 秒级；
    /// 要把所有磁盘翻一遍，用设置页「检测 Java」窗口里的「深度扫描」（默认 30 秒预算、可中断）。
    /// </summary>
    public IReadOnlyList<JavaRuntime> GetJavaRuntimes(bool forceRefresh = false)
    {
        if (forceRefresh || _javaCache is null)
        {
            _javaCache = Java.Detect(deepSearch: false);
        }

        return _javaCache;
    }

    public void InvalidateJavaCache() => _javaCache = null;

    /// <summary>把已检测到的 Java 列表写回缓存（检测窗口用，避免关闭后再次全盘扫描）。</summary>
    public void SetJavaCache(IReadOnlyList<JavaRuntime> runtimes) => _javaCache = runtimes;

    /// <summary>
    /// 深度扫描 Java（遍历用户目录与各磁盘，受 <see cref="JavaRuntimeService.DeepScanBudget"/> 限制，默认 30 秒），
    /// 结果与当前缓存合并后写回，返回合并后的完整列表。
    /// </summary>
    public IReadOnlyList<JavaRuntime> DeepScanJava(
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var found = Java.Detect(deepSearch: true, onProgress: onProgress, cancellationToken: cancellationToken);
        var merged = (_javaCache ?? [])
            .Concat(found)
            .GroupBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(j => j.MajorVersion)
            .ThenBy(j => j.JavaPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetJavaCache(merged);
        return merged;
    }

    public GameInstallationService Installation { get; }

    public ModpackInstaller Modpacks { get; }

    public GameLaunchService Launch { get; }

    /// <summary>内置服务器数据（供启动页的「热门服务器推荐」使用）。</summary>
    /// <summary>本地目录缓存（服务器列表的唯一数据来源）。</summary>
    public CatalogStore Catalog { get; }

    /// <summary>远端目录服务（启动检查 / 定时检查 / 立即刷新 / 事件上报）。</summary>
    public RemoteCatalogService RemoteCatalog { get; }

    /// <summary>服务器图标缓存（列表按需拉取，离线用上次缓存）。</summary>
    public IconCache Icons { get; }

    /// <summary>启动页底部的游戏提示。</summary>
    public TipsService Tips { get; }

    /// <summary>广告服务（拉取广告位内容、事件上报、不感兴趣）。</summary>
    public AdService Ads { get; }

    /// <summary>本地保存的账户列表。</summary>
    public List<AccountProfile> AccountList { get; private set; }

    /// <summary>当前选中的账户。</summary>
    public AccountProfile? CurrentAccount
    {
        get => _currentAccount;
        set
        {
            if (Equals(_currentAccount, value))
            {
                return;
            }

            _currentAccount = value;
            AccountChanged?.Invoke(this, EventArgs.Empty);
            PersistSelection();
        }
    }

    private AccountProfile? _currentAccount;

    /// <summary>当前账户变化（切换账户类型 / 登录成功）时触发，皮肤页与启动页据此刷新。</summary>
    public event EventHandler? AccountChanged;

    /// <summary>当前选中的 Java。</summary>
    public JavaRuntime? CurrentJava
    {
        get => _currentJava;
        set
        {
            if (Equals(_currentJava, value))
            {
                return;
            }

            _currentJava = value;
            PersistSelection();
        }
    }

    private JavaRuntime? _currentJava;

    /// <summary>当前选中的游戏版本。</summary>
    public string? CurrentVersionId
    {
        get => _currentVersionId;
        set
        {
            if (string.Equals(_currentVersionId, value, StringComparison.Ordinal))
            {
                return;
            }

            _currentVersionId = value;
            PersistSelection();
        }
    }

    private string? _currentVersionId;

    public string GameRoot => Settings.ResolveGameDirectory();

    /// <summary>启动所需条件是否齐备。</summary>
    public bool IsReadyToLaunch =>
        CurrentAccount is not null
        && CurrentJava is not null
        && !string.IsNullOrWhiteSpace(CurrentVersionId);

    public void SaveAccounts(IEnumerable<AccountProfile> accounts)
    {
        AccountList = accounts.ToList();
        Accounts.Save(AccountList);
    }

    public void AddAccount(AccountProfile profile)
    {
        AccountList = Accounts.Upsert(profile).ToList();
        CurrentAccount = profile;
    }

    /// <summary>重新从本地读一遍账户列表（登录 / 登出 / 删除之后调用）。</summary>
    public void RefreshAccounts() => AccountList = Accounts.Load()
        .Where(a => AppEdition.AllowsOfflineLogin || a.Kind != AccountKind.离线)
        .ToList();

    /// <summary>切换到某个已保存的账户。</summary>
    public void SwitchAccount(AccountProfile profile) => CurrentAccount = profile;

    /// <summary>登出：清掉该账户的 token（条目保留，显示为「已登出」）。</summary>
    public void SignOutAccount(AccountProfile profile)
    {
        AccountList = Accounts.SignOut(profile).ToList();
        SyncCurrentAfterChange(profile);
    }

    /// <summary>删除该账户；如果它就是当前账户，则当前账户变成"未登录"。</summary>
    public void RemoveAccount(AccountProfile profile)
    {
        AccountList = Accounts.Remove(profile).ToList();
        SyncCurrentAfterChange(profile);
    }

    /// <summary>
    /// 登出/删除之后把 CurrentAccount 指向列表里的新对象（登出）或清空（删除）——
    /// 否则界面上还挂着已经失效的那份快照（token 还在内存里）。
    /// </summary>
    private void SyncCurrentAfterChange(AccountProfile profile)
    {
        if (CurrentAccount is null ||
            CurrentAccount.Kind != profile.Kind ||
            !string.Equals(CurrentAccount.Name, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentAccount = AccountList.FirstOrDefault(a =>
            a.Kind == profile.Kind && string.Equals(a.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
    }
}
