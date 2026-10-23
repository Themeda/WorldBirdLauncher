using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>
/// 微软正版登录：设备代码流 → Xbox Live → XSTS → Minecraft 服务 → 正版档案。
///
/// 2026-10-20 用户确认：阉割版只保留「正版 / 外置 / 统一通行证」三种登录，
/// 所以把原先只在探针脚本里跑通的链路正式接进客户端。
///
/// ★ 当前**仍卡在 Mojang 的允许清单**：第 ④ 步会返回
/// <c>403 Invalid app registration</c>，这不是 Azure 配置错（①–③ 都能过）。
/// 审核通过前，这里会把错误翻译成"应用正在 Mojang 审核中"，避免用户误以为账号有问题。
/// </summary>
public static class MicrosoftAuth
{
    /// <summary>项目使用的 Azure 应用 Client ID（与完整版 / 探针脚本一致）。</summary>
    public const string ClientId = "3f799499-d783-4ca5-939c-f9aad81fdc46";

    private const string Scope = "XboxLive.signin offline_access";
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MCSL/0.1 (+https://github.com/Themeda/MCSL)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>设备代码流返回给用户看的信息。</summary>
    public sealed record DeviceCode(
        string DeviceCodeValue,
        string UserCode,
        string VerificationUri,
        int ExpiresInSeconds,
        int IntervalSeconds);

    /// <summary>微软 OAuth token（refresh_token 用于下次免授权续期）。</summary>
    public sealed record MicrosoftToken(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

    /// <summary>申请一次设备代码（用户在浏览器里输 code 授权）。</summary>
    public static async Task<DeviceCode> StartDeviceCodeAsync(CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scope"] = Scope,
        });
        using var response = await Http.PostAsync(DeviceCodeUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthException(
                "申请微软登录代码失败：" + ExtractOAuthError(body));
        }

        using var document = TryParse(body) ??
                             throw new MicrosoftAuthException("微软返回的设备代码不是合法 JSON，请稍后重试。");
        var root = document.RootElement;
        var deviceCode = GetString(root, "device_code");
        var userCode = GetString(root, "user_code");
        var uri = GetString(root, "verification_uri");
        if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(userCode) ||
            string.IsNullOrWhiteSpace(uri))
        {
            throw new MicrosoftAuthException("微软没有返回有效的设备代码，请稍后重试。");
        }

        return new DeviceCode(
            deviceCode!,
            userCode!,
            uri!,
            GetInt(root, "expires_in", 900),
            Math.Max(2, GetInt(root, "interval", 5)));
    }

    /// <summary>
    /// 轮询直到用户完成授权。取消令牌用于用户关窗 / 取消登录。
    /// <paramref name="status"/> 会收到"等待授权 / 网络稍慢"等进度文字。
    /// </summary>
    public static async Task<MicrosoftToken> PollForTokenAsync(
        DeviceCode device,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        var interval = Math.Max(2, device.IntervalSeconds);
        var deadline = DateTimeOffset.Now.AddSeconds(Math.Max(60, device.ExpiresInSeconds));
        var pendingCount = 0;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken).ConfigureAwait(false);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = device.DeviceCodeValue,
            });
            using var response = await Http.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = TryParse(body) ??
                                 throw new MicrosoftAuthException("微软授权响应不是合法 JSON，请稍后重试。");
            var root = document.RootElement;
            var accessToken = GetString(root, "access_token");
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                return new MicrosoftToken(
                    accessToken!,
                    GetString(root, "refresh_token"),
                    GetInt(root, "expires_in", 3600));
            }

            var error = GetString(root, "error") ?? string.Empty;
            switch (error)
            {
                case "authorization_pending":
                    pendingCount++;
                    status?.Report(pendingCount % 6 == 0
                        ? "还在等你完成授权…（浏览器里输入代码并点「继续」）"
                        : "等待微软授权中…");
                    continue;

                case "slow_down":
                    interval = Math.Min(15, interval + 5);
                    status?.Report("授权服务器要求放慢轮询，继续等待…");
                    continue;

                case "authorization_declined":
                    throw new MicrosoftAuthException("你在微软页面上拒绝了本次授权。");

                case "expired_token":
                    throw new MicrosoftAuthException("设备代码已过期，请点「重新获取代码」再试。");

                default:
                    throw new MicrosoftAuthException(
                        "微软授权失败：" + (GetString(root, "error_description") ?? error));
            }
        }

        throw new MicrosoftAuthException("等待授权超时（设备代码已过期），请重新获取代码。");
    }

    /// <summary>用 refresh_token 免授权换新 token（启动时静默续期用）。</summary>
    public static async Task<MicrosoftToken> RefreshAsync(
        string refreshToken, CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        });
        using var response = await Http.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthException("微软登录已过期，请重新登录：" + ExtractOAuthError(body));
        }

        using var document = TryParse(body) ??
                             throw new MicrosoftAuthException("微软续期响应不是合法 JSON，请重新登录。");
        var root = document.RootElement;
        var access = GetString(root, "access_token");
        if (string.IsNullOrWhiteSpace(access))
        {
            throw new MicrosoftAuthException("微软没有返回新的 access_token，请重新登录。");
        }

        return new MicrosoftToken(
            access!,
            GetString(root, "refresh_token") ?? refreshToken,
            GetInt(root, "expires_in", 3600));
    }

    /// <summary>把微软 token 换成 Minecraft 正版档案（XBL → XSTS → login_with_xbox → profile）。</summary>
    public static async Task<AccountProfile> CompleteAsync(
        MicrosoftToken token, CancellationToken cancellationToken = default)
    {
        // ② Xbox Live 用户认证（MSA token 前面要加 d=）
        using var xbl = await PostJsonAsync(XblUrl, new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + token.AccessToken,
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
        }, cancellationToken).ConfigureAwait(false);
        var xblToken = GetString(xbl.RootElement, "Token");
        var userHash = FirstDisplayClaim(xbl.RootElement, "uhs");
        if (string.IsNullOrWhiteSpace(xblToken) || string.IsNullOrWhiteSpace(userHash))
        {
            throw new MicrosoftAuthException("Xbox Live 认证失败：微软返回的内容不完整。");
        }

        // ③ XSTS
        string xstsToken;
        try
        {
            using var xsts = await PostJsonAsync(XstsUrl, new
            {
                Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
                RelyingParty = "rp://api.minecraftservices.com/",
                TokenType = "JWT",
            }, cancellationToken).ConfigureAwait(false);
            xstsToken = GetString(xsts.RootElement, "Token") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(xstsToken))
            {
                throw new MicrosoftAuthException("XSTS 授权失败：" + DescribeXstsError(xsts.RootElement));
            }
        }
        catch (HttpRequestException)
        {
            throw new MicrosoftAuthException(
                "XSTS 授权失败：连不上 Xbox 服务。请检查网络（部分网络需要代理）后重试。");
        }

        // ④ 换 Minecraft access token —— 目前会卡在这一步（Mojang 允许清单）
        using var mcResponse = await Http.PostAsync(
            McLoginUrl,
            JsonContent(new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" }),
            cancellationToken).ConfigureAwait(false);
        var mcBody = await mcResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var mcDocument = TryParse(mcBody);
        var mcAccess = mcDocument is null ? null : GetString(mcDocument.RootElement, "access_token");
        if (!mcResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(mcAccess))
        {
            var message = mcDocument is null
                ? Shorten(mcBody)
                : GetString(mcDocument.RootElement, "errorMessage") ??
                  GetString(mcDocument.RootElement, "error") ?? Shorten(mcBody);
            if (message.Contains("Invalid app registration", StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftAuthException(
                    "微软账号本身已验证通过，但本启动器的应用仍在 Mojang 的 Java 版允许清单审核中" +
                    "（403 Invalid app registration）。审核通过前无法完成正版登录，请先用外置登录或统一通行证。");
            }

            throw new MicrosoftAuthException(
                $"Minecraft 登录失败（HTTP {(int)mcResponse.StatusCode}）：{message}");
        }

        // ⑤ 正版档案（游戏名 / UUID / 皮肤）
        using var request = new HttpRequestMessage(HttpMethod.Get, McProfileUrl);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", mcAccess);
        using var profileResponse = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var profileBody = await profileResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var profileDocument = TryParse(profileBody);
        var name = profileDocument is null ? null : GetString(profileDocument.RootElement, "name");
        var uuid = profileDocument is null ? null : GetString(profileDocument.RootElement, "id");
        if (!profileResponse.IsSuccessStatusCode || string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(uuid))
        {
            throw new MicrosoftAuthException(
                "读取正版档案失败：这个微软账号可能还没有购买 Minecraft Java 版。");
        }

        return new AccountProfile
        {
            Kind = AccountKind.微软,
            Name = name!,
            Uuid = uuid!,
            AccessToken = mcAccess!,
            ClientToken = string.Empty,
            MicrosoftRefreshToken = token.RefreshToken,
            MicrosoftExpiresAt = DateTimeOffset.Now.AddSeconds(Math.Max(60, token.ExpiresInSeconds)),
        };
    }

    // ---------- 小工具 ----------

    private static async Task<JsonDocument> PostJsonAsync(
        string url, object body, CancellationToken cancellationToken)
    {
        using var response = await Http.PostAsync(url, JsonContent(body), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftAuthException(
                $"认证失败（HTTP {(int)response.StatusCode}）：{Shorten(text)}");
        }

        return TryParse(text) ??
               throw new MicrosoftAuthException($"认证失败：返回内容不是合法 JSON：{Shorten(text)}");
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static string? FirstDisplayClaim(JsonElement root, string name)
    {
        if (!root.TryGetProperty("DisplayClaims", out var claims) ||
            !claims.TryGetProperty("xui", out var xui) ||
            xui.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in xui.EnumerateArray())
        {
            var value = GetString(item, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string DescribeXstsError(JsonElement root)
    {
        var code = root.TryGetProperty("XErr", out var xerr) && xerr.TryGetInt64(out var value)
            ? value
            : 0;
        return code switch
        {
            2148916233 => "这个微软账号还没有 Xbox 档案，请先用同一账号登录 https://www.xbox.com/ 创建档案。",
            2148916235 => "这个账号所在地区不支持 Xbox Live。",
            2148916238 => "未成年账号需要在家庭组里获得批准。",
            _ => code == 0 ? "未知错误。" : $"XErr={code}。",
        };
    }

    private static string ExtractOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var description = GetString(document.RootElement, "error_description");
            var error = GetString(document.RootElement, "error");
            return (description ?? error ?? body).Replace("\r", " ").Replace("\n", " ").Trim();
        }
        catch (JsonException)
        {
            return Shorten(body);
        }
    }

    private static JsonDocument? TryParse(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Shorten(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? "（响应为空）"
            : text.Length <= 200 ? text : text[..200] + "…";

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int GetInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var element) && element.TryGetInt32(out var value) ? value : fallback;
}

/// <summary>微软登录失败（带面向用户的中文说明）。</summary>
public sealed class MicrosoftAuthException : Exception
{
    public MicrosoftAuthException(string message) : base(message)
    {
    }
}
