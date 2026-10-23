using System.Windows.Media;
using System.Windows.Media.Imaging;
using McServerList.App.Ads;
using McServerList.App.Infrastructure;
using McServerList.App.Services;
using McServerList.Core.Models;

namespace McServerList.App.ViewModels;

/// <summary>服务器卡片视图模型：静态数据 + 实时状态。</summary>
public sealed class ServerCardViewModel : ObservableObject
{
    private ServerStatus? _status;
    private bool _isFavorite;
    private ImageSource? _icon;
    private string? _iconSourceData;

    public ServerCardViewModel(ServerEntry entry)
    {
        Entry = entry;
    }

    public ServerEntry Entry { get; }

    public string Id => Entry.Id;

    public string Name => ServerTextFormatter.StripColorCodes(Entry.Name);

    public string Address => Entry.Address;

    public string EndPoint => Entry.EndPoint;

    public string Version => ServerTextFormatter.StripColorCodes(Entry.Version);

    public string Loader => Entry.Loader;

    /// <summary>地区（国内 / 国外）。</summary>
    public string RegionText => Entry.Region;

    public IReadOnlyList<string> Categories => Entry.Categories
        .Select(ServerTextFormatter.StripColorCodes)
        .Where(c => c.Length > 0)
        .ToList();

    public string Description => ServerTextFormatter.StripColorCodes(Entry.Description);

    private string? _motd;

    /// <summary>
    /// 服务端采样到的 MOTD（**保留 § 颜色代码与换行**）。
    /// 卡片上用 MotdTextBlock 渲染（替换原来的"简介"那一行；没有 MOTD 时整行收起）。
    /// </summary>
    public string? Motd
    {
        get => _motd;
        private set
        {
            if (SetProperty(ref _motd, value))
            {
                OnPropertyChanged(nameof(HasMotd));
                OnPropertyChanged(nameof(MotdVisibility));
            }
        }
    }

    public bool HasMotd => !string.IsNullOrWhiteSpace(_motd);

    // ---------- 排序加成（置顶卡 / 刷新卡，2026-10-09 第五十一轮） ----------

    /// <summary>加成种类：top（置顶卡）/ refresh（刷新卡）/ 空表示没有加成。</summary>
    public string BoostKind { get; private set; } = string.Empty;

    /// <summary>服务端算好的加成名次（0 表示没有加成）。</summary>
    public int BoostRank { get; private set; }

    /// <summary>加成到期时间。</summary>
    public DateTimeOffset? BoostUntil { get; private set; }

    /// <summary>最近一次用刷新卡的日期（详情页"最近活跃"；没用过为 null）。</summary>
    public DateTimeOffset? LastRefreshedAt { get; private set; }

    /// <summary>排序用：置顶=2，刷新=1，没有=0。</summary>
    public int BoostLevel => BoostKind switch
    {
        "top" => 2,
        "refresh" => 1,
        _ => 0,
    };

    /// <summary>只有置顶卡会显示「置顶」角标（刷新卡按用户要求不标记）。</summary>
    public System.Windows.Visibility TopBadgeVisibility =>
        BoostKind == "top" ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>「最近活跃」文案（没用过刷新卡时为空，详情页整行不显示）。</summary>
    public string LastActiveText =>
        LastRefreshedAt is { } at ? $"最近活跃：{at:yyyy-MM-dd}" : string.Empty;

    public System.Windows.Visibility LastActiveVisibility => LastRefreshedAt is null
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    /// <summary>应用服务端下发的加成（没有就清空）。</summary>
    public void ApplyBoost(string? kind, int rank, DateTimeOffset? until, DateTimeOffset? lastRefreshedAt)
    {
        BoostKind = kind ?? string.Empty;
        BoostRank = rank;
        BoostUntil = until;
        LastRefreshedAt = lastRefreshedAt;
        OnPropertyChanged(nameof(BoostKind));
        OnPropertyChanged(nameof(BoostRank));
        OnPropertyChanged(nameof(BoostLevel));
        OnPropertyChanged(nameof(TopBadgeVisibility));
        OnPropertyChanged(nameof(LastActiveText));
        OnPropertyChanged(nameof(LastActiveVisibility));
    }

    public System.Windows.Visibility MotdVisibility =>
        HasMotd ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // ---------- 推广卡（广告只从广告接口来，独立插入固定位置，不参与排序与筛选） ----------

    private AdItem? _ad;

    /// <summary>这条卡片是不是推广卡。</summary>
    public bool IsAd => _ad is not null;

    public AdItem? Ad => _ad;

    public System.Windows.Visibility AdBadgeVisibility =>
        IsAd ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility AdActionVisibility =>
        IsAd ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>把这张卡标记成推广卡（卡片模板会显示「推广」角标与「不感兴趣」）。</summary>
    public void MarkAsAd(AdItem ad)
    {
        _ad = ad;
        // 推广卡用"广告描述"填原来 MOTD 那一行（没有描述就整行收起），保证卡片有信息量
        if (!string.IsNullOrWhiteSpace(ad.Description))
        {
            Motd = ad.Description;
        }

        OnPropertyChanged(nameof(IsAd));
        OnPropertyChanged(nameof(Ad));
        OnPropertyChanged(nameof(AdBadgeVisibility));
        OnPropertyChanged(nameof(AdActionVisibility));
    }

    public string LongDescription =>
        ServerTextFormatter.ToParagraphs(
            string.IsNullOrWhiteSpace(Entry.LongDescription) ? Entry.Description : Entry.LongDescription);

    public string? SourceUrl => Entry.SourceUrl;

    /// <summary>
    /// 截图地址。后台抓取/上传的图托管在服务端，条目里存的是**相对地址**（uploads/xxx.png，
    /// 这样开发/生产/子路径都通用）→ 这里补上服务端基址再给界面用。
    /// </summary>
    public IReadOnlyList<string> ScreenshotUrls => Entry.ScreenshotUrls
        .Select(ToAbsoluteUrl)
        .Where(url => url.Length > 0)
        .ToList();

    private static string ToAbsoluteUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url ?? string.Empty;
        }

        return AppInfo.CatalogEndpoint.TrimEnd('/') + "/" + url.TrimStart('/');
    }

    public bool HasScreenshots => Entry.ScreenshotUrls.Count > 0;

    // ---------- 收录信息（正版验证 / 白名单 / 登录方式 / 专属客户端 …） ----------

    /// <summary>是否有可直连地址；没有地址的条目只能走官网 / QQ 群 / 专属客户端。</summary>
    public bool HasAddress => Entry.HasAddress;

    /// <summary>该服务器的全部线路（主线路 / 备用 / 联通 …）。</summary>
    public IReadOnlyList<ServerAddress> Addresses => Entry.AllAddresses;

    private IReadOnlyList<string>? _modGroups;

    /// <summary>
    /// 从介绍文本归纳出的"热门模组"分组（科技 / 魔法 / 冒险 / 农业 / 装饰 / 辅助），用于筛选。
    /// </summary>
    public IReadOnlyList<string> ModGroups => _modGroups ??= BuildModGroups();

    private static readonly (string Keyword, string Group)[] ModGroupMap =
    [
        ("工业", "科技"), ("科技", "科技"), ("机械动力", "科技"), ("Create", "科技"),
        ("应用能源", "科技"), ("AE2", "科技"), ("热力", "科技"), ("沉浸", "科技"),
        ("魔法", "魔法"), ("神秘", "魔法"), ("植物魔法", "魔法"), ("星辉", "魔法"), ("血魔法", "魔法"),
        ("冒险", "冒险"), ("地牢", "冒险"), ("精英怪", "冒险"), ("拔刀", "冒险"), ("匠魂", "冒险"),
        ("农业", "农业"), ("农夫", "农业"), ("潘马斯", "农业"), ("烹饪", "农业"),
        ("建筑", "装饰"), ("装饰", "装饰"), ("家具", "装饰"), ("建筑工坊", "装饰"),
        ("辅助", "辅助"), ("JEI", "辅助"), ("小地图", "辅助"), ("优化", "辅助"), ("连锁", "辅助"),
    ];

    private IReadOnlyList<string> BuildModGroups()
    {
        var text = Entry.LongDescription + " " + Entry.Description;
        return ModGroupMap
            .Where(m => text.Contains(m.Keyword, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Group)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public bool HasMultipleAddresses => Entry.HasMultipleAddresses;

    public System.Windows.Visibility MultiAddressVisibility =>
        Entry.HasMultipleAddresses ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility NoAddressVisibility =>
        HasAddress ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public bool RequiresCustomClient => Entry.RequiresCustomClient;

    public System.Windows.Visibility CustomClientVisibility =>
        RequiresCustomClient ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string PremiumText => Entry.RequirePremium switch
    {
        true => "需正版验证",
        false => "无正版验证",
        _ => "验证方式未标注",
    };

    public string WhitelistText => Entry.Whitelist switch
    {
        true => "需白名单",
        false => "无白名单",
        _ => "白名单未标注",
    };

    public string LoginModeText => string.IsNullOrWhiteSpace(Entry.LoginMode)
        ? "未标注"
        : ServerTextFormatter.StripColorCodes(Entry.LoginMode);

    public string ServerTypeText => string.IsNullOrWhiteSpace(Entry.ServerType)
        ? "未标注"
        : ServerTextFormatter.StripColorCodes(Entry.ServerType);

    public string LinesText => Entry.Lines.Count > 0 ? string.Join(" / ", Entry.Lines) : "未标注";

    public string RatingText => Entry.Rating is { } rating ? $"{rating:0.0} 分" : "暂无评分";

    public string SourceNameText => string.IsNullOrWhiteSpace(Entry.SourceName)
        ? "本地内置数据"
        : Entry.SourceName!;

    public string QqGroupText => string.IsNullOrWhiteSpace(Entry.QqGroup) ? "未提供" : Entry.QqGroup!;

    public bool HasQqGroup => !string.IsNullOrWhiteSpace(Entry.QqGroup);

    public string OfficialSiteText => string.IsNullOrWhiteSpace(Entry.OfficialSite) ? "未提供" : Entry.OfficialSite!;

    public bool HasOfficialSite => !string.IsNullOrWhiteSpace(Entry.OfficialSite);

    public bool HasClientDownload => !string.IsNullOrWhiteSpace(Entry.ClientDownloadUrl);

    /// <summary>
    /// 专属客户端的下载链接**列表**（支持多条网盘链接）；
    /// 新数据用 <c>clientDownloads</c> 数组，老数据回退到单条 clientDownloadUrl。
    /// </summary>
    public IReadOnlyList<ClientDownload> ClientDownloads
    {
        get
        {
            if (Entry.ClientDownloads is { Count: > 0 })
            {
                return Entry.ClientDownloads.Where(d => !string.IsNullOrWhiteSpace(d.Url)).ToList();
            }

            return string.IsNullOrWhiteSpace(Entry.ClientDownloadUrl)
                ? []
                : [new ClientDownload
                {
                    Label = "下载",
                    Url = Entry.ClientDownloadUrl!,
                    ExtractCode = Entry.ClientExtractCode,
                    Note = Entry.ClientNote,
                }];
        }
    }

    public bool HasClientDownloads => ClientDownloads.Count > 0;

    public string ClientDownloadText => string.IsNullOrWhiteSpace(Entry.ClientDownloadUrl)
        ? "未提供下载链接"
        : Entry.ClientDownloadUrl!;

    public string ClientExtractCodeText => string.IsNullOrWhiteSpace(Entry.ClientExtractCode)
        ? "—"
        : Entry.ClientExtractCode!;

    public string ClientNoteText => string.IsNullOrWhiteSpace(Entry.ClientNote) ? string.Empty : Entry.ClientNote!;

    public System.Windows.Visibility ClientNoteVisibility => string.IsNullOrWhiteSpace(Entry.ClientNote)
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    /// <summary>无直连地址时给用户的进入方式提示。</summary>
    public string AddressHint => HasAddress
        ? string.Empty
        : "该服务器未公开直连地址，请通过下方「官网 / QQ 群 / 专属客户端」进入。";

    public ServerStatus? Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                RaiseStatusDependents();
            }
        }
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteButtonText));
            }
        }
    }

    public string FavoriteButtonText => IsFavorite ? "已收藏" : "收藏";

    public ImageSource? Icon
    {
        get
        {
            // 优先用服务端下发/本地缓存的图标；详情页本机 ping 到的 favicon 作为补充
            var current = _cachedIconDataUri ?? _status?.FaviconDataUri;
            if (!string.Equals(_iconSourceData, current, StringComparison.Ordinal))
            {
                _iconSourceData = current;
                _icon = LoadFavicon(current);
            }

            return _icon;
        }
    }

    private string? _cachedIconDataUri;

    /// <summary>把从图标缓存拿到的图片字节装进 ViewModel（列表按需调用）。</summary>
    public void ApplyIconBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return;
        }

        _cachedIconDataUri = "data:image/png;base64," + Convert.ToBase64String(bytes);
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }

    public bool HasIcon => Icon is not null;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public bool IsOnline => Status?.IsOnline == true;

    /// <summary>查过但没连上（可能只是一时关服，列表里要显著标出来）。</summary>
    public bool IsOffline => Status is { IsOnline: false };

    public bool IsChecking { get; set; }

    private string _signalLevel = StatusLevels.Unknown;

    /// <summary>
    /// 网络信号等级（服务端采样下发）：good/fair/poor/offline/no-address/unknown。
    /// 按需求列表**只显示等级**，具体延迟只在详情页「测试线路延迟」里由本机实测。
    /// </summary>
    public string SignalLevel
    {
        get => _signalLevel;
        set
        {
            if (SetProperty(ref _signalLevel, value))
            {
                OnPropertyChanged(nameof(SignalText));
                OnPropertyChanged(nameof(SignalBars));
                OnPropertyChanged(nameof(LatencyText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string SignalText => StatusLevels.DisplayName(SignalLevel);

    public string SignalBars => StatusLevels.Bars(SignalLevel);

    // ---------- 本机实测信号（用户 2026-10-10：列表显示"本机 ↔ 游戏服务器"的信号） ----------

    /// <summary>本机 ping 到的信号等级；null = 还没测过（卡片退回显示服务端等级）。</summary>
    public string? LocalLevel { get; private set; }

    /// <summary>本机实测延迟（毫秒）；连不上时为 null。</summary>
    public long? LocalLatencyMs { get; private set; }

    /// <summary>本机实测时间。</summary>
    public DateTimeOffset? LocalMeasuredAt { get; private set; }

    /// <summary>卡片/列表显示用的信号等级：**本机实测优先**，没测过才用服务端下发的。</summary>
    public string DisplaySignalLevel => LocalLevel ?? SignalLevel;

    /// <summary>卡片上那行小字：显示本机实测等级（没测过就显示服务端等级）。</summary>
    public string DisplaySignalText => StatusLevels.DisplayName(DisplaySignalLevel);

    /// <summary>卡片上的三格信号图标用的等级。</summary>
    public string DisplaySignalBars => StatusLevels.Bars(DisplaySignalLevel);

    /// <summary>详情页的对比文案：「本机实测 良好（42 ms）／ 服务端采样 一般」。</summary>
    public string SignalCompareText => LocalLevel is null
        ? $"本机实测 未测 ／ 服务端采样 {StatusLevels.DisplayName(SignalLevel)}"
        : $"本机实测 {StatusLevels.DisplayName(LocalLevel)}"
          + (LocalLatencyMs is { } ms ? $"（{ms} ms）" : string.Empty)
          + $" ／ 服务端采样 {StatusLevels.DisplayName(SignalLevel)}"
          + (LocalMeasuredAt is { } at ? $"（{at:MM-dd HH:mm} 本机实测）" : string.Empty);

    /// <summary>应用一次本机实测结果。</summary>
    public void ApplyLocalLatency(string? level, long? milliseconds, DateTimeOffset? measuredAt)
    {
        LocalLevel = level;
        LocalLatencyMs = milliseconds;
        LocalMeasuredAt = measuredAt;
        OnPropertyChanged(nameof(LocalLevel));
        OnPropertyChanged(nameof(LocalLatencyMs));
        OnPropertyChanged(nameof(LocalMeasuredAt));
        OnPropertyChanged(nameof(DisplaySignalLevel));
        OnPropertyChanged(nameof(DisplaySignalText));
        OnPropertyChanged(nameof(DisplaySignalBars));
        OnPropertyChanged(nameof(SignalCompareText));
        OnPropertyChanged(nameof(LatencyText));
    }

    /// <summary>服务端采样到的在线人数（列表/详情显示）。</summary>
    public int? SampledPlayers { get; private set; }

    public int? SampledMaxPlayers { get; private set; }

    /// <summary>应用一次服务端下发的状态（只含等级/人数/版本，没有延迟）。</summary>
    public void ApplyServerStatus(string level, int? playersOnline, int? playersMax, string? versionText, string? motd = null)
    {
        SignalLevel = level;
        SampledPlayers = playersOnline;
        SampledMaxPlayers = playersMax;
        if (!string.IsNullOrWhiteSpace(versionText))
        {
            ServerVersionText = versionText;
        }

        // 服务端采样失败时 MOTD 为空，这时保留上一次的（服务端也是 COALESCE 保留上一份）
        if (!string.IsNullOrWhiteSpace(motd))
        {
            Motd = motd;
        }

        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(ServerSoftware));
    }

    /// <summary>服务端采样到的服务端版本串（用于"服务端软件"一行）。</summary>
    public string? ServerVersionText { get; private set; }

    /// <summary>已被服务端下架、但因为收藏过/玩过而保留在本地的条目（显示为"离线"）。</summary>
    public bool IsRetained { get; private set; }

    /// <summary>标记为"已下架保留"：状态一律显示离线，并提供手动移除入口。</summary>
    public void MarkRetained()
    {
        IsRetained = true;
        SignalLevel = StatusLevels.Offline;
        OnPropertyChanged(nameof(IsRetained));
        OnPropertyChanged(nameof(RetainedHint));
    }

    public string RetainedHint => IsRetained ? "该服务器已下架（你收藏或游玩过，所以保留在这里）" : string.Empty;

    public string PlayersText =>
        SampledPlayers is not null
            ? $"{SampledPlayers} / {SampledMaxPlayers?.ToString() ?? "?"}"
            : Status is { IsOnline: true, OnlinePlayers: not null }
                ? $"{Status.OnlinePlayers} / {Status.MaxPlayers?.ToString() ?? "?"}"
                : "— / —";

    /// <summary>按需求这里显示"信号等级"而不是毫秒数。</summary>
    public string LatencyText =>
        DisplaySignalLevel == StatusLevels.Unknown && Status is null ? "—" : DisplaySignalText;

    public string StatusText => SignalLevel switch
    {
        StatusLevels.Good or StatusLevels.Fair or StatusLevels.Poor => "在线",
        StatusLevels.Offline => "断联",
        StatusLevels.NoAddress => "未公开地址",
        _ => "待采样",
    };

    public string ServerSoftware =>
        ServerTextFormatter.StripColorCodes(ServerVersionText ?? Status?.VersionName ?? Version);

    /// <summary>Ping 返回的服务端软件名（如 Velocity 代理版本串）；与数据里的游戏版本不同才有意义。</summary>
    public string SoftwareText
    {
        get
        {
            var software = ServerTextFormatter.StripColorCodes(Status?.VersionName);
            return string.IsNullOrWhiteSpace(software) ||
                   string.Equals(software, Version, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : software;
        }
    }

    public System.Windows.Visibility SoftwareVisibility => string.IsNullOrEmpty(SoftwareText)
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    /// <summary>跨版本服务器支持的范围（如 1.8.x ~ 1.19.x）；为空表示普通服务器。</summary>
    public string VersionRangeText => string.IsNullOrWhiteSpace(Entry.VersionRange) ? string.Empty : Entry.VersionRange!;

    public System.Windows.Visibility VersionRangeVisibility => string.IsNullOrWhiteSpace(Entry.VersionRange)
        ? System.Windows.Visibility.Collapsed
        : System.Windows.Visibility.Visible;

    public string PlayersListText =>
        Status?.Players is { Count: > 0 } players
            ? string.Join("、", players.Select(ServerTextFormatter.StripColorCodes))
            : "该服务器未提供在线玩家名单";

    /// <summary>
    /// 玩家名单的说明文字（放在"当前在线玩家"标题下方，不改标题本身）。
    /// 人数（OnlinePlayers）是服务端上报的全量计数；名单要么来自 Query（较完整），
    /// 要么来自 Ping 的 sample（协议层面通常最多 12 个），所以经常"人数很多、名单很少"。
    /// </summary>
    public string PlayersListHint => Status?.Source.HasFlag(Core.Models.StatusSource.Query) == true
        ? "完整名单来自服务器 Query 查询。"
        : "样本，非完整名单";

    public string MapText => string.IsNullOrWhiteSpace(Status?.Map)
        ? "未提供"
        : ServerTextFormatter.StripColorCodes(Status!.Map);

    public string PluginsText => string.IsNullOrWhiteSpace(Status?.Plugins)
        ? "未提供"
        : ServerTextFormatter.StripColorCodes(Status!.Plugins);

    public void NotifyStatusChanged() => RaiseStatusDependents();

    private void RaiseStatusDependents()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SignalText));
        OnPropertyChanged(nameof(SignalBars));
        OnPropertyChanged(nameof(ServerSoftware));
        OnPropertyChanged(nameof(SoftwareText));
        OnPropertyChanged(nameof(SoftwareVisibility));
        OnPropertyChanged(nameof(PlayersListText));
        OnPropertyChanged(nameof(PlayersListHint));
        OnPropertyChanged(nameof(MapText));
        OnPropertyChanged(nameof(PluginsText));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
    }

    private static ImageSource? LoadFavicon(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return null;
        }

        try
        {
            var commaIndex = dataUri!.IndexOf(',');
            if (commaIndex < 0)
            {
                return null;
            }

            var bytes = Convert.FromBase64String(dataUri[(commaIndex + 1)..]);
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
}
