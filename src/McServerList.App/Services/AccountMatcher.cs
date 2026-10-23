using McServerList.Core.Launcher;
using McServerList.Core.Models;

namespace McServerList.App.Services;

/// <summary>
/// 按服务器条目推断"要进这台服务器得用哪种账户"。
///
/// 用在「最近玩过的服务器」选中（= 直连）时：当前用的是统一通行证账户，
/// 但勾的这台是皮肤站 / 正版服，就要自动把账号切成对应类型（用户 2026-10-08 要求）。
/// </summary>
internal static class AccountMatcher
{
    /// <summary>该服务器需要的账户类型；标注不足时返回 null（表示"不动用户当前账户"）。</summary>
    public static AccountKind? RequiredKind(ServerEntry entry)
    {
        var login = (entry.LoginMode ?? string.Empty).ToLowerInvariant();
        if (login.Contains("统一通行证") || login.Contains("unifiedpass"))
        {
            return AccountKind.统一通行证;
        }

        if (login.Contains("外置") || login.Contains("皮肤站") || login.Contains("yggdrasil") ||
            login.Contains("littleskin") || login.Contains("skin"))
        {
            return AccountKind.外置;
        }

        if (login.Contains("正版"))
        {
            return AccountKind.微软;
        }

        if (login.Contains("离线"))
        {
            // 阉割版没有离线账户：不强制切换，用户用现有的正版/外置/统一通行证账户即可进入
            return AppEdition.AllowsOfflineLogin ? AccountKind.离线 : null;
        }

        // 登录方式没写清楚时看「是否需正版验证」：需要 = 微软，不需要 = 离线
        return entry.RequirePremium switch
        {
            true => AccountKind.微软,
            false => AppEdition.AllowsOfflineLogin ? AccountKind.离线 : null,
            _ => null,
        };
    }

    /// <summary>给用户看的账户类型名（用在状态提示里）。</summary>
    public static string Describe(AccountKind kind) => kind switch
    {
        AccountKind.微软 => "微软正版",
        AccountKind.外置 => "外置登录（皮肤站）",
        AccountKind.统一通行证 => "统一通行证",
        _ => "离线",
    };
}
