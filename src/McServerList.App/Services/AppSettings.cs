using System.Text.Json;

namespace McServerList.App.Services;

/// <summary>用户配置（JSON 持久化）。</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>是否启用 Query 协议补充查询。</summary>
    public bool EnableQuery { get; set; } = true;

    /// <summary>状态缓存时长（秒）。</summary>
    public int CacheSeconds { get; set; } = 60;

    /// <summary>Minecraft 游戏目录（.minecraft）；为空时使用默认检测结果。</summary>
    public string? GameDirectory { get; set; }

    /// <summary>是否使用 BMCLAPI 镜像下载游戏与 Java（国内推荐）。</summary>
    public bool UseMirror { get; set; } = true;

    /// <summary>游戏最大内存（MB）。</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>微软登录使用的 Azure 应用 ClientId（可留空）。</summary>
    public string? MicrosoftClientId { get; set; }


    /// <summary>默认启动后进入的服务器地址（可在设置页配置）。</summary>
    public string? DefaultServerAddress { get; set; }

    /// <summary>是否启用版本隔离。</summary>
    public bool VersionIsolation { get; set; } = true;

    /// <summary>启动游戏后自动最小化到托盘（游戏退出后自动显示）。</summary>
    public bool TrayAfterLaunch { get; set; } = true;

    /// <summary>点击关闭按钮时最小化到托盘而非退出程序（默认关闭，只有托盘菜单才能真正退出）。</summary>
    public bool TrayOnClose { get; set; }

    /// <summary>同时进行的下载任务数（默认 2 个"异线程双任务"下载）。</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>皮肤展示页的 3D 预览背景：dark（深夜网格）/ sky（天空草地）。</summary>
    public string SkinBackground { get; set; } = "dark";

    /// <summary>界面主题：light（浅色）/ dark（深色）/ system（跟随系统）。</summary>
    public string ThemeMode { get; set; } = "light";

    /// <summary>上次使用的账户类型（配合 <see cref="LastAccountName"/> 在启动时恢复）。</summary>
    public string? LastAccountKind { get; set; }

    /// <summary>上次使用的账户名。</summary>
    public string? LastAccountName { get; set; }

    /// <summary>上次选择的游戏版本 id。</summary>
    public string? LastVersionId { get; set; }

    /// <summary>
    /// 「最近玩过的服务器」里被选中（语义同直连）的服务器 id。
    /// 下次启动时自动勾选，并把直连地址与账号重新适配成这台需要的方式。
    /// </summary>
    public string? LastRecentServerId { get; set; }

    /// <summary>上次使用的服务器列表排序（0 在线人数 / 1 信号 / 2 名称 / 3 热度 / 4 收藏优先）。</summary>
    public int LastServerSort { get; set; }

    /// <summary>上次选择的 Java（javaw 完整路径）。</summary>
    public string? LastJavaPath { get; set; }

    /// <summary>默认游戏目录：优先用户指定，其次 %APPDATA%\.minecraft。</summary>
    public string ResolveGameDirectory()
    {
        if (!string.IsNullOrWhiteSpace(GameDirectory))
        {
            return GameDirectory!;
        }

        var detected = DetectGameDirectory();
        if (detected is not null)
        {
            return detected;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    }

    /// <summary>
    /// 自动探测已有的 .minecraft 目录（优先含 versions 子目录的），
    /// 覆盖"官方启动器装在 AppData"和"启动器放在 D 盘"两类常见情况。
    /// </summary>
    public static string? DetectGameDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"),
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;
            foreach (var folder in new[] { "Minecraft", "我的世界", "Games", "游戏", "PCL", "MC" })
            {
                candidates.Add(Path.Combine(root, folder, ".minecraft"));
            }

            candidates.Add(Path.Combine(root, ".minecraft"));
        }

        // 优先选择已经装了版本的目录
        // 注意：AppSettings 自身有名为 Directory 的静态属性，这里必须写全名
        var existing = candidates
            .Where(System.IO.Directory.Exists)
            .ToList();

        var withVersions = existing.FirstOrDefault(dir =>
            System.IO.Directory.Exists(Path.Combine(dir, "versions")) &&
            System.IO.Directory.EnumerateDirectories(Path.Combine(dir, "versions")).Any());

        return withVersions ?? existing.FirstOrDefault();
    }

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "McServerList");

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // 配置损坏时回退到默认值
        }

        return new AppSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }

}
