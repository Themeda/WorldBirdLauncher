namespace WorldBirdLauncher.App.Services;

/// <summary>应用级常量。</summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/Themeda/WorldBirdLauncher";

    /// <summary>
    /// 服务器列表目录服务端地址（**写死，界面不提供修改**）。
    ///
    /// 目前是本地测试服务端（`dotnet run --project src/WorldBirdLauncher.Server`）；
    /// 部署到 VPS 后只需要改这一行常量（换成正式域名，例如 https://api.example.com）。
    /// </summary>
    /// <summary>
    /// 目录服务端地址。VPS 上没有域名、用反向代理映射到子路径 <c>/mcsl-server/</c>，
    /// 所以这里带上前缀：请求会打到 <c>http://121.41.81.251/mcsl-server/api/v1/…</c>。
    /// （本地开发调试时改回 http://localhost:5080 即可。）
    /// </summary>
    public const string CatalogEndpoint = "http://121.41.81.251/mcsl-server";

    /// <summary>上报接口的内置 key（只防误用，不是安全边界）。</summary>
    public const string CatalogClientKey = "mcsl-local-dev-key";

    /// <summary>
    /// 官网「服主入口」页。客户端设置页的「提交服务器」改成跳这里——
    /// 引导用户注册/登录后，在服主后台提交新服务器（用户 2026-10-10 要求）。
    /// </summary>
    public const string AdvertiserEntryUrl = "http://121.41.81.251/advertiser/";

    /// <summary>客户端版本（匿名指标上报时带上，用来分版本看数据）。</summary>
    public static string ClientVersion =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string SubmitIssueUrl()
    {
        var body = """
                   服务器名称：
                   服务器地址（ip 或 域名，含端口）：
                   支持版本（如 1.20.x / 26.x）：
                   加载器（原版 / Forge / Fabric / NeoForge）：
                   玩法类型（生存 / 小游戏 / RPG / 建筑 / 模组服 等）：
                   一句话简介：
                   服务器官网或介绍页：

                   - [ ] 我确认该服务器公开可加入，且信息真实
                   """;

        return $"{RepositoryUrl}/issues/new?labels=服务器提交" +
               $"&title={Uri.EscapeDataString("[服务器提交] ")}" +
               $"&body={Uri.EscapeDataString(body)}";
    }
}
