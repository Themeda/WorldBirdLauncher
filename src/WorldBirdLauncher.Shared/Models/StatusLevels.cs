namespace WorldBirdLauncher.Core.Models;

/// <summary>
/// 网络信号等级（服务端采样 → 下发给客户端展示）。
///
/// 按用户要求：**列表只显示等级，不显示具体延迟**；具体 ms 只在详情页点「测试线路延迟」时
/// 由客户端本机实测。阈值：良好 &lt; 150ms；一般 150–400ms；较差 &gt; 400ms；离线 = 无响应/超时。
/// </summary>
public static class StatusLevels
{
    public const string Good = "good";
    public const string Fair = "fair";
    public const string Poor = "poor";
    public const string Offline = "offline";

    /// <summary>条目没有公开直连地址（需官网/QQ群/专属客户端进入），不参与采样。</summary>
    public const string NoAddress = "no-address";

    /// <summary>服务端还没采过这台的样。</summary>
    public const string Unknown = "unknown";

    public const int GoodMaxMilliseconds = 150;
    public const int FairMaxMilliseconds = 400;

    /// <summary>由实测延迟得出等级（null = 没连上）。</summary>
    public static string FromLatency(long? latencyMs) =>
        latencyMs is null ? Offline
        : latencyMs < GoodMaxMilliseconds ? Good
        : latencyMs <= FairMaxMilliseconds ? Fair
        : Poor;

    /// <summary>界面文案。</summary>
    public static string DisplayName(string? level) => level switch
    {
        Good => "良好",
        Fair => "一般",
        Poor => "较差",
        Offline => "离线",
        NoAddress => "未公开地址",
        _ => "待采样",
    };

    /// <summary>排序用的权重：良好 → 一般 → 较差 → 离线 → 待采样 → 未公开地址（无地址的排最后）。</summary>
    public static int Rank(string? level) => level switch
    {
        Good => 0,
        Fair => 1,
        Poor => 2,
        Offline => 3,
        Unknown => 4,
        _ => 5,
    };

    /// <summary>四格信号（列表里比文字更直观；未知/断联是空心格）。</summary>
    public static string Bars(string? level) => level switch
    {
        Good => "●●●●",
        Fair => "●●●○",
        Poor => "●●○○",
        _ => "○○○○",
    };

    /// <summary>是否算"能连上"（良好/较差/很差）。</summary>
    public static bool IsOnline(string? level) => level is Good or Fair or Poor;
}
