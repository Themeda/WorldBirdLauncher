using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>
/// 账户登录与本地保存。
///
/// 登录能力**全部自研**：离线（官方离线 UUID 规则）、外置 Yggdrasil（<see cref="YggdrasilAuth"/>）、
/// 统一通行证（<see cref="UnifiedPassAuth"/>）；微软登录尚未开放（需要 Azure ClientId）。
/// 本地存储使用 Windows DPAPI（当前用户作用域）加密，避免明文落盘。
/// </summary>
public sealed class AccountService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public AccountService(string? storeDirectory = null)
    {
        StoreDirectory = storeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "McServerList");
    }

    public string StoreDirectory { get; }

    private string StorePath => Path.Combine(StoreDirectory, "accounts.dat");

    /// <summary>读取本地保存的账户列表。</summary>
    public IReadOnlyList<AccountProfile> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return [];
            }

            var protectedBytes = File.ReadAllBytes(StorePath);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<List<AccountProfile>>(plain, Options) ?? [];
        }
        catch (Exception)
        {
            // 换机器 / 数据损坏时从空开始，不影响使用
            return [];
        }
    }

    public void Save(IEnumerable<AccountProfile> accounts)
    {
        Directory.CreateDirectory(StoreDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList(), Options);
        var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, protectedBytes);
    }

    /// <summary>离线登录（用户名即游戏内 ID）。</summary>
    public AccountProfile LoginOffline(string username)
    {
        var name = username.Trim();
        return new AccountProfile
        {
            Kind = AccountKind.离线,
            Name = name,
            Uuid = OfflineUuid(name),
            AccessToken = Guid.NewGuid().ToString("D"),
            ClientToken = Guid.NewGuid().ToString("D"),
        };
    }

    /// <summary>
    /// 官方离线 UUID：<c>MD5("OfflinePlayer:" + 名字)</c> 打上 RFC 4122 的版本/变体位，按**大端十六进制**拼成带横线的 UUID。
    /// 参考值：<c>Notch → b50ad385-829d-3141-a216-7e7d7539ba7f</c>。
    ///
    /// 注意：改造前 StarLight 的 <c>GenerateNameUuid</c> 虽然也算 MD5，但用了 <c>new Guid(bytes)</c>——
    /// .NET 会把前三段按小端解释，算出来的是**字节序错掉**的 UUID（和服务器/其它启动器算的不一样）。
    /// 服务端的离线 UUID 判定（部分插件用它做数据绑定）认的是官方值，所以这里按官方规则实现。
    /// </summary>
    internal static string OfflineUuid(string name)
    {
        var hash = NetCompat.Md5(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // 版本 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // variant
        var hex = NetCompat.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>外置登录（Yggdrasil / authlib-injector，例如 LittleSkin、自定义皮肤站）。</summary>
    public async Task<AccountProfile> LoginYggdrasilAsync(
        string serverUrl,
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await YggdrasilAuth.LoginAsync(serverUrl, email, password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>统一通行证登录（Nide8）。</summary>
    public async Task<AccountProfile> LoginUnifiedPassAsync(
        string serverId,
        string baseUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await UnifiedPassAuth
            .LoginAsync(baseUrl, serverId, username, password, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>把登录结果写入本地（覆盖同名账户）。</summary>
    public IReadOnlyList<AccountProfile> Upsert(AccountProfile profile)
    {
        var list = Load().ToList();
        list.RemoveAll(a => string.Equals(a.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
                            && a.Kind == profile.Kind);
        list.Insert(0, profile);
        Save(list);
        return list;
    }

    /// <summary>
    /// 登出：清掉这个账户的 token（**条目保留**在列表里，界面显示为「已登出」，之后可以重新登录）。
    /// 离线账户没有 token，原样保留。
    /// </summary>
    public IReadOnlyList<AccountProfile> SignOut(AccountProfile profile)
    {
        var signedOut = profile.Kind == AccountKind.离线
            ? profile
            : profile with
            {
                AccessToken = string.Empty,
                ClientToken = string.Empty,
                MicrosoftRefreshToken = null,
                MicrosoftExpiresAt = null,
            };
        return Upsert(signedOut);
    }

    /// <summary>
    /// 微软账户静默续期：用 refresh_token 重新走一遍 XBL/XSTS/Minecraft（拿到新的正版 token）。
    /// 失败时抛 <see cref="MicrosoftAuthException"/>，调用方决定是否提示用户重新登录。
    /// </summary>
    public async Task<AccountProfile> RefreshMicrosoftAsync(
        AccountProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Kind != AccountKind.微软 || string.IsNullOrWhiteSpace(profile.MicrosoftRefreshToken))
        {
            throw new MicrosoftAuthException("这个账户没有可用于续期的微软凭据，请重新登录。");
        }

        var token = await MicrosoftAuth.RefreshAsync(profile.MicrosoftRefreshToken!, cancellationToken)
            .ConfigureAwait(false);
        var refreshed = await MicrosoftAuth.CompleteAsync(token, cancellationToken).ConfigureAwait(false);
        Upsert(refreshed);
        return refreshed;
    }

    /// <summary>从本地列表里删除一个账户（同名 + 同登录方式算同一个）。</summary>
    public IReadOnlyList<AccountProfile> Remove(AccountProfile profile)
    {
        var list = Load().ToList();
        list.RemoveAll(a => string.Equals(a.Name, profile.Name, StringComparison.OrdinalIgnoreCase)
                            && a.Kind == profile.Kind);
        Save(list);
        return list;
    }

    /// <summary>这个账户是不是「已登出」（离线账户没有 token，永远算已登录）。</summary>
    public static bool IsSignedOut(AccountProfile profile) =>
        profile.Kind != AccountKind.离线 && string.IsNullOrEmpty(profile.AccessToken);
}
