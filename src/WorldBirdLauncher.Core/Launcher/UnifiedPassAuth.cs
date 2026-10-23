using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// 统一通行证（Nide8）登录——自研，替代 StarLight 的 <c>UnifiedPassAuthenticator</c>。
///
/// 接口就是 Yggdrasil 的那一套：<c>POST {站点}/&lt;服务器ID&gt;/authserver/authenticate</c>。
/// 两个踩过的坑（见 AGENTS.md 已踩坑清单）：
/// ① 成功响应里 UUID 字段是 <c>selectedProfile.id</c>，StarLight 的模型读的是 <c>uuid</c> → 拿到的永远是空；
/// ② 失败时服务端返回 <c>{{"error":"ForbiddenOperationException","errorMessage":"密码错误"}}</c>（中文），
///    要把它原样翻给用户，别只报 HTTP 状态码。
/// </summary>
public static class UnifiedPassAuth
{
    /// <summary>默认站点（登录用；注意注入器 nide8auth.jar 是另一个域名 login.mc-user.com）。</summary>
    public const string DefaultBaseUrl = "https://auth.mc-user.com:233/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>用统一通行证账号登录，返回可用的账户档案。</summary>
    public static async Task<AccountProfile> LoginAsync(
        string baseUrl,
        string serverId,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new InvalidOperationException("请填写服务器 ID（32 位，由服主提供）。");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("请填写用户名与密码。");
        }

        var url = BuildAuthenticateUrl(baseUrl, serverId);
        using var content = new StringContent(BuildRequestBody(username, password), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        string body;
        try
        {
            response = await Http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException($"连不上统一通行证服务器（{url}）：{ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(TranslateError(response.StatusCode, body));
            }

            return ParseResponse(body, serverId);
        }
    }

    /// <summary>拼登录地址：站点地址可以是「域名」「域名:端口」「带协议」三种写法，都会补成 <c>/&lt;服务器ID&gt;/authserver/authenticate</c>。</summary>
    internal static string BuildAuthenticateUrl(string baseUrl, string serverId)
    {
        var normalized = NormalizeBaseUrl(baseUrl);
        return new Uri(new Uri(normalized), Uri.EscapeDataString(serverId.Trim()) + "/authserver/authenticate").ToString();
    }

    /// <summary>站点地址归一化：没写协议就补 https，没写结尾斜杠就补上。</summary>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim();
        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        return value.EndsWith('/') ? value : value + "/";
    }

    /// <summary>请求体（与 Yggdrasil 一致；agent 用我们自己的名字，实测服务端不校验）。</summary>
    internal static string BuildRequestBody(string username, string password) =>
        JsonSerializer.Serialize(new
        {
            agent = new { name = "WorldBirdLauncher", version = GameArgumentsBuilder.DefaultLauncherVersion },
            username,
            password,
            clientToken = (string?)null,
            requestUser = true,
        });

    /// <summary>解析成功响应；拿不到 selectedProfile 就当成失败（服务端有时会 200 返回别的形状）。</summary>
    internal static AccountProfile ParseResponse(string json, string serverId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("selectedProfile", out var profile) || profile.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("通行证服务器没有返回角色信息：" + Summarize(json));
        }

        // ★ 坑：字段名是 id（不是 uuid）
        var uuid = profile.TryGetProperty("id", out var idNode) ? idNode.GetString() : null;
        var name = profile.TryGetProperty("name", out var nameNode) ? nameNode.GetString() : null;
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("通行证返回的角色信息不完整：" + Summarize(json));
        }

        return new AccountProfile
        {
            Kind = AccountKind.统一通行证,
            Name = name!,
            Uuid = uuid!,
            AccessToken = root.TryGetProperty("accessToken", out var token) ? token.GetString() ?? string.Empty : string.Empty,
            ClientToken = root.TryGetProperty("clientToken", out var client) ? client.GetString() ?? string.Empty : string.Empty,
            UnifiedPassServerId = serverId.Trim(),
        };
    }

    /// <summary>把失败响应翻成人话：优先用服务端的 errorMessage（它本来就是中文），其次按状态码兜底。</summary>
    internal static string TranslateError(HttpStatusCode status, string body)
    {
        string? message = null;
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                message = document.RootElement.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
                code = document.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就走下面的兜底
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            return status switch
            {
                HttpStatusCode.Forbidden => $"登录失败：{message}（账号或密码不对，也可能是该账号不属于这个服务器 ID）",
                HttpStatusCode.NotFound => $"登录失败：{message}（服务器 ID 可能不对）",
                _ => $"登录失败：{message}",
            };
        }

        return status switch
        {
            HttpStatusCode.Forbidden => "登录失败：账号或密码错误" + (code is null ? string.Empty : $"（{code}）"),
            HttpStatusCode.NotFound => $"登录失败：服务器 ID 不存在或站点地址不对（HTTP {(int)status}）",
            _ => $"登录失败：通行证服务器返回 HTTP {(int)status}",
        };
    }

    private static string Summarize(string json) =>
        json.Length <= 200 ? json : json[..200] + "…";
}
