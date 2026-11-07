using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// Server metadata still records login modes for display. The public launcher
/// supports Microsoft authentication only, so external-account servers are
/// clearly identified and blocked instead of being started with a mismatched account.
/// </summary>
internal static class AccountMatcher
{
    public const string UnsupportedExternalAccountMessage =
        "该服务器需要外部账户，当前公开版仅支持 Microsoft 账户，无法启动。";

    public static bool RequiresExternalAccount(ServerEntry entry)
    {
        var login = (entry.LoginMode ?? string.Empty).ToLowerInvariant();
        return login.Contains("统一通行证") ||
               login.Contains("unifiedpass") ||
               login.Contains("nide8") ||
               login.Contains("外置") ||
               login.Contains("皮肤站") ||
               login.Contains("yggdrasil") ||
               login.Contains("littleskin") ||
               login.Contains("authlib");
    }
}
