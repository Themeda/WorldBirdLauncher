namespace WorldBirdLauncher.Core.Launcher;

/// <summary>一个可用的 Java 运行时。</summary>
public sealed record JavaRuntime(
    string JavaPath,
    string LibraryPath,
    int MajorVersion,
    string Version,
    bool Is64Bit)
{
    public string DisplayName => $"Java {MajorVersion}" + (Is64Bit ? " (64位)" : " (32位)");
}

/// <summary>Java 下载信息。</summary>
public sealed record JavaDownloadInfo(
    int FeatureVersion,
    string ReleaseName,
    string FileName,
    string Url,
    string? Sha256,
    long SizeBytes)
{
    public double SizeMb => Math.Round(SizeBytes / 1024d / 1024d, 1);
}

/// <summary>安装 / 下载进度。</summary>
public sealed record InstallProgress(
    string Stage,
    int Percentage,
    string? Detail = null,
    string? File = null,
    long FileDone = 0,
    long FileTotal = 0,
    double BytesPerSecond = 0,
    TimeSpan? Remaining = null)
{
    /// <summary>百分比为负表示"步骤耗时不可估算"（界面用不确定态进度条）。</summary>
    public bool IsIndeterminate => Percentage < 0;
}

/// <summary>统一的安装结果（屏蔽 StarLight.Core 各安装器返回类型的差异）。</summary>
public sealed record InstallOutcome(bool Success, string Message, string? VersionId = null)
{
    public static InstallOutcome Ok(string versionId, string message) => new(true, message, versionId);

    public static InstallOutcome Fail(string message) => new(false, message);
}

/// <summary>单个模组的下载结果。</summary>
public sealed record ModInstallOutcome(bool Success, string Message, string? FilePath = null)
{
    public static ModInstallOutcome Ok(string filePath, string message) => new(true, message, filePath);

    public static ModInstallOutcome Fail(string message) => new(false, message);
}

/// <summary>游戏版本来源。</summary>
public enum VersionSource
{
    本地已安装,
    远程可下载,
}

/// <summary>一个游戏版本（本地或远程）。</summary>
public sealed record GameVersionInfo(string Id, string DisplayName, VersionSource Source, string? Type = null)
{
    public bool IsInstalled => Source == VersionSource.本地已安装;

    /// <summary>
    /// 本地目录是否可用（有同名版本 JSON）。false 表示只有个 jar 的"残缺目录"，
    /// 这种目录不能启动，只能在列表里当提示。
    /// </summary>
    public bool IsUsable { get; init; } = true;
}

/// <summary>整合包格式。</summary>
public enum ModpackFormat
{
    /// <summary>未知 / 无法识别。</summary>
    Unknown,

    /// <summary>离线完整包：压缩包内直接是 .minecraft 结构（versions / mods / config 等）。</summary>
    OfflineArchive,

    /// <summary>CurseForge 整合包：manifest.json + overrides/。</summary>
    CurseForge,

    /// <summary>Modrinth 整合包：modrinth.index.json + overrides/。</summary>
    Modrinth,

    /// <summary>MultiMC / Prism 整合包：mmc-pack.json + instance.cfg（本质也是离线包）。</summary>
    MultiMc,
}

/// <summary>整合包中的一个待处理文件。</summary>
public sealed record ModpackFile(
    string RelativePath,
    string? DownloadUrl,
    long SizeBytes,
    string? Sha1 = null,
    bool FromArchive = false)
{
    /// <summary>是否为压缩包内的本地文件（相对压缩包根目录）。</summary>
    public bool IsFromArchive => FromArchive;

    /// <summary>需要联网下载但当前无法解析下载地址（例如缺少 CurseForge API Key）。</summary>
    public bool IsUnresolved => !FromArchive && string.IsNullOrWhiteSpace(DownloadUrl);
}

/// <summary>整合包解析结果。</summary>
public sealed record ModpackInfo
{
    public required ModpackFormat Format { get; init; }

    public required string Name { get; init; }

    public string? MinecraftVersion { get; init; }

    /// <summary>加载器名称（原版 / Fabric / Forge / NeoForge / Quilt）。</summary>
    public string? Loader { get; init; }

    public string? LoaderVersion { get; init; }

    public string? Description { get; init; }

    /// <summary>需要下载的文件（CurseForge / Modrinth 格式才有）。</summary>
    public IReadOnlyList<ModpackFile> RemoteFiles { get; init; } = [];

    /// <summary>压缩包内需要解压的本地文件。</summary>
    public IReadOnlyList<ModpackFile> ArchiveFiles { get; init; } = [];

    /// <summary>需要下载但当前拿不到地址的文件数（CurseForge 需要 API Key）。</summary>
    public int UnresolvedFileCount => RemoteFiles.Count(f => f.IsUnresolved);

    public int TotalFileCount => RemoteFiles.Count + ArchiveFiles.Count;

    public string Summary =>
        $"{Format}｜MC {MinecraftVersion ?? "未知"}｜{Loader ?? "原版"}" +
        (string.IsNullOrWhiteSpace(LoaderVersion) ? "" : $" {LoaderVersion}") +
        $"｜共 {TotalFileCount} 个文件";
}

/// <summary>整合包安装结果。</summary>
public sealed record ModpackInstallResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    /// <summary>安装后生成的版本 ID（可直接用于启动）。</summary>
    public string? InstanceId { get; init; }

    /// <summary>因缺少 CurseForge API Key 等原因未能下载的文件数。</summary>
    public int SkippedFiles { get; init; }

    /// <summary>没能下载的文件清单（相对路径），用于给用户一个明确的提示。</summary>
    public IReadOnlyList<string> FailedFiles { get; init; } = [];
}

/// <summary>账户类型（与 StarLight.Core 的 AuthType 对应，便于持久化）。</summary>
public enum AccountKind
{
    离线,
    微软,
    外置,
    统一通行证,
}

/// <summary>本地保存的账户档案。</summary>
public sealed record AccountProfile
{
    public required AccountKind Kind { get; init; }

    public required string Name { get; init; }

    public string Uuid { get; init; } = string.Empty;

    public string AccessToken { get; init; } = string.Empty;

    public string ClientToken { get; init; } = string.Empty;

    /// <summary>外置登录的皮肤站地址（Yggdrasil）。</summary>
    public string? ServerUrl { get; init; }

    /// <summary>统一通行证的服务器 ID。</summary>
    public string? UnifiedPassServerId { get; init; }

    /// <summary>
    /// 微软账户的 MSA refresh_token（只在 DPAPI 加密的 accounts.dat 里保存，用于免授权续期）。
    /// </summary>
    public string? MicrosoftRefreshToken { get; init; }

    /// <summary>微软账户的 Minecraft access token 到期时间（到期后可静默续期）。</summary>
    public DateTimeOffset? MicrosoftExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string DisplayText => Kind switch
    {
        AccountKind.离线 => $"{Name}（离线）",
        AccountKind.微软 => $"{Name}（微软正版）",
        AccountKind.外置 => $"{Name}（外置 · {ServerUrl}）",
        AccountKind.统一通行证 => $"{Name}（统一通行证）",
        _ => Name,
    };
}

/// <summary>启动请求。</summary>
public sealed record GameLaunchRequest
{
    public required string GameRoot { get; init; }

    public required string VersionId { get; init; }

    public required string JavaPath { get; init; }

    public required AccountProfile Account { get; init; }

    public int MaxMemoryMb { get; init; } = 4096;

    public int MinMemoryMb { get; init; } = 512;

    public int WindowWidth { get; init; } = 854;

    public int WindowHeight { get; init; } = 480;

    public bool IsFullScreen { get; init; }

    public bool IsVersionIsolation { get; init; } = true;

    /// <summary>启动后自动进入的服务器地址（形如 <c>ip</c> 或 <c>ip:port</c>）。</summary>
    public string? ServerAddress { get; init; }
}

/// <summary>启动结果。</summary>
public sealed record GameLaunchOutcome
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public int? ProcessId { get; init; }

    /// <summary>实际使用的启动参数（便于排查）。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>游戏进程（供调用方跟踪"游戏是否已退出"，例如托盘恢复窗口）。</summary>
    public System.Diagnostics.Process? Process { get; init; }
}
