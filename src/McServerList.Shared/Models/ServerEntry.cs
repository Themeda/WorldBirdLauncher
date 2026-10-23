namespace McServerList.Core.Models;

/// <summary>服务器的一条线路地址（主线路 / 备用 / 联通 / 电信 …）。</summary>
public sealed record ServerAddress(string Label, string Host, int Port)
{
    /// <summary>主机与端口的展示形式。</summary>
    public string EndPoint => Port == 25565 ? Host : $"{Host}:{Port}";
}

/// <summary>专属客户端的一条下载链接（网盘等）。</summary>
public sealed record ClientDownload
{
    /// <summary>名称/来源，如「百度网盘」「蓝奏云」「QQ 群文件」。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>下载地址（网盘链接）。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>提取码（可空）。</summary>
    public string? ExtractCode { get; init; }

    /// <summary>补充说明（可空），如「含 mods + config，解压即用」。</summary>
    public string? Note { get; init; }

    /// <summary>展示用文案（名称 + 提取码），供列表/详情页直接绑定。</summary>
    public string DisplayText =>
        string.IsNullOrWhiteSpace(ExtractCode)
            ? (string.IsNullOrWhiteSpace(Label) ? Url : $"{Label}：{Url}")
            : (string.IsNullOrWhiteSpace(Label) ? $"{Url}（提取码 {ExtractCode}）" : $"{Label}：{Url}（提取码 {ExtractCode}）");
}

/// <summary>
/// 列表展示用的服务器条目（来自内置静态数据或远程数据源）。
/// </summary>
public sealed class ServerEntry
{
    /// <summary>稳定唯一标识，用于收藏、连接历史等本地数据关联。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>服务器显示名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>服务器地址（域名或 IP，不含端口）。</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>服务器端口，默认 25565。</summary>
    public int Port { get; init; } = 25565;

    /// <summary>展示用游戏版本描述，如 "1.20.x"。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>跨版本服务器支持的范围（如 "1.8.x ~ 1.19.x"）；为空表示只支持 <see cref="Version"/>。</summary>
    public string? VersionRange { get; init; }

    /// <summary>版本筛选分组，取值来自 <see cref="ServerCategories.VersionGroups"/>。</summary>
    public IReadOnlyList<string> VersionGroups { get; init; } = Array.Empty<string>();

    /// <summary>加载器类型：原版 / Forge / Fabric / NeoForge。</summary>
    public string Loader { get; init; } = "原版";

    /// <summary>所在地区：国内 / 国外（用于列表区分）。</summary>
    public string Region { get; init; } = ServerCategories.RegionOverseas;

    /// <summary>玩法类型标签，如 生存、小游戏、RPG。</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>简要描述（列表卡片用）。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>完整描述（详情页用）。</summary>
    public string LongDescription { get; init; } = string.Empty;

    /// <summary>图标地址（远程 URL）或 data URI；为空时使用默认图标。</summary>
    public string? IconUrl { get; init; }

    /// <summary>数据来源页面，便于溯源与人工核对。</summary>
    public string? SourceUrl { get; init; }

    /// <summary>截图地址（详情页展示，可为空）。</summary>
    public IReadOnlyList<string> ScreenshotUrls { get; init; } = Array.Empty<string>();

    /// <summary>查询状态时使用的 Query 协议端口；为空表示与 <see cref="Port"/> 相同。</summary>
    public int? QueryPort { get; init; }

    // ---------- 收录信息（主要来自 MC百科「找服玩」等数据源） ----------

    /// <summary>是否需要正版验证；<c>null</c> 表示来源未标注。</summary>
    public bool? RequirePremium { get; init; }

    /// <summary>是否开启白名单；<c>null</c> 表示来源未标注。</summary>
    public bool? Whitelist { get; init; }

    /// <summary>登录方式补充说明（如「外置登录（LittleSkin）」「统一通行证」）；为空时按正版验证推导。</summary>
    public string? LoginMode { get; init; }

    /// <summary>服务器类型：公益服 / 商业服；为空表示未标注。</summary>
    public string? ServerType { get; init; }

    /// <summary>线路标签：电信 / 联通 / 移动 / 海外。</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>是否需要作者提供的专属客户端才能进入。</summary>
    public bool RequiresCustomClient { get; init; }

    /// <summary>专属客户端下载地址（网盘等）。</summary>
    public string? ClientDownloadUrl { get; init; }

    /// <summary>
    /// 专属客户端的**多条**下载链接（网盘等，服主自行维护）。
    /// 为空时客户端会回退到上面的单条 <see cref="ClientDownloadUrl"/>。
    /// </summary>
    public IReadOnlyList<ClientDownload> ClientDownloads { get; init; } = Array.Empty<ClientDownload>();

    /// <summary>网盘提取码。</summary>
    public string? ClientExtractCode { get; init; }

    /// <summary>客户端获取方式补充说明（如「群文件 / Wiki 里下载」）。</summary>
    public string? ClientNote { get; init; }

    /// <summary>官方 QQ 群号。</summary>
    public string? QqGroup { get; init; }

    /// <summary>官方网站。</summary>
    public string? OfficialSite { get; init; }

    /// <summary>来源站点评分（MC百科为 0–10）；<c>null</c> 表示暂无评分。</summary>
    public double? Rating { get; init; }

    /// <summary>数据来源站点名称（如 MC百科 找服玩 / MCBBS 纪念版）。</summary>
    public string? SourceName { get; init; }

    /// <summary>是否有可直连的地址（无地址的条目只能走专属客户端/官网/QQ群）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasAddress => !string.IsNullOrWhiteSpace(Address);

    /// <summary>多线路地址（第一项约定为主线路）；为空表示只有 <see cref="Address"/> 一条。</summary>
    public IReadOnlyList<ServerAddress> Addresses { get; init; } = Array.Empty<ServerAddress>();

    /// <summary>全部可用线路（含把 <see cref="Address"/> 兜底成「主线路」）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<ServerAddress> AllAddresses
    {
        get
        {
            if (Addresses.Count > 0)
            {
                return Addresses;
            }

            return HasAddress ? [new ServerAddress("主线路", Address, Port)] : [];
        }
    }

    /// <summary>是否配置了多条线路。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasMultipleAddresses => Addresses.Count > 1;

    /// <summary>是否为软件内置数据。</summary>
    public bool IsBuiltIn { get; init; } = true;

    /// <summary>主机与端口的展示形式。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EndPoint => Port == 25565 ? Address : $"{Address}:{Port}";
}
