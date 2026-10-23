namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 发行版开关（2026-10-20 用户确认）。
///
/// 完整版（WBL-Pro.exe）与 Lite 版（WBL.exe）**共用同一套 Core/Shared**，
/// 差异只在 App 层：阉割版由 <c>-p:LiteEdition=true</c> 打开 <c>LITE</c> 编译常量，
/// 关掉离线登录、游戏版本下载与广告位；付费/广告的服务端代码保持原样，后期把开关关掉即可恢复。
/// </summary>
public static class AppEdition
{
#if LITE
    public const bool IsLite = true;

    /// <summary>界面上展示的产品名。</summary>
    public const string ProductName = "WorldBirdLauncher";
#else
    public const bool IsLite = false;
    public const string ProductName = "WorldBirdLauncher-Pro";
#endif

    /// <summary>阉割版不提供离线账户（用户 2026-10-20 确认：只保留正版 / 外置 / 统一通行证）。</summary>
    public static bool AllowsOfflineLogin => !IsLite;

    /// <summary>阉割版不提供"安装游戏版本"（只保留模组/光影下载与自带游戏本体的整合包）。</summary>
    public static bool AllowsVersionDownload => !IsLite;

    /// <summary>阉割版不展示广告位（置顶/刷新卡排序仍保留）。</summary>
    public static bool ShowsAds => !IsLite;
}
