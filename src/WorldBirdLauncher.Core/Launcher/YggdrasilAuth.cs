using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// 外置登录（Yggdrasil / authlib-injector）。
///
/// 不用 StarLight.Core 的 YggdrasilAuthenticator：它的实现里把皮肤站地址**写死**成
/// <c>https://littleskin.cn/api/yggdrasil</c>（IL 可验证），传进去的地址根本不起作用；
/// 而且它把服务端返回的业务错误（例如 403 ForbiddenOperationException「用户名或密码错误」）
/// 直接当成网络异常抛出，用户只能看到"在发送 HTTP POST 请求时发生异常"。
/// 这里自己实现：地址可配置（自动补协议与 /api/yggdrasil）、错误信息如实反馈。
/// </summary>
public static class YggdrasilAuth
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.1 (github.com/Themeda/WorldBirdLauncher)");
        return client;
    }

    /// <summary>登录并返回账户档案；失败时抛出带中文说明的异常。</summary>
    public static async Task<AccountProfile> LoginAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveApiRootAsync(serverUrl, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidOperationException(
                       $"外置登录失败：无法识别皮肤站地址「{serverUrl}」。\n" +
                       "请填皮肤站的 API 根地址，例如 https://littleskin.cn/api/yggdrasil，或直接填域名让程序自动识别。");

        var payload = JsonSerializer.Serialize(new
        {
            agent = new { name = "Minecraft", version = 1 },
            username,
            password,
            clientToken = Guid.NewGuid().ToString("N"),
            requestUser = true,
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await Http
            .PostAsync($"{root}/authserver/authenticate", content, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(BuildErrorMessage(response.StatusCode, body));
        }

        using var document = JsonDocument.Parse(body);
        var rootElement = document.RootElement;
        var accessToken = GetString(rootElement, "accessToken");
        var clientToken = GetString(rootElement, "clientToken");
        string name = string.Empty;
        string uuid = string.Empty;
        if (rootElement.TryGetProperty("selectedProfile", out var profile) && profile.ValueKind == JsonValueKind.Object)
        {
            name = GetString(profile, "name");
            uuid = GetString(profile, "id");
        }

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("外置登录失败：皮肤站没有返回有效的账户信息（可能该账号未创建角色）。");
        }

        return new AccountProfile
        {
            Kind = AccountKind.外置,
            Name = name,
            Uuid = uuid,
            AccessToken = accessToken,
            ClientToken = clientToken,
            ServerUrl = root,
        };
    }

    /// <summary>
    /// 解析皮肤站 API 根地址：支持"只填域名""带协议""直接给 API 根"三种写法。
    /// 判定标准是 GET 该地址能返回带 meta 的 JSON（Yggdrasil API 根特征）。
    /// </summary>
    public static async Task<string?> ResolveApiRootAsync(string input, CancellationToken cancellationToken = default)
    {
        var candidate = (input ?? string.Empty).Trim().TrimEnd('/');
        if (candidate.Length == 0)
        {
            return null;
        }

        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate;
        }

        foreach (var root in new[] { candidate, candidate + "/api/yggdrasil" })
        {
            if (await LooksLikeApiRootAsync(root, cancellationToken).ConfigureAwait(false))
            {
                return root;
            }
        }

        return null;
    }

    private static async Task<bool> LooksLikeApiRootAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("meta", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把 Yggdrasil 的服务端错误翻译成人话。</summary>
    private static string BuildErrorMessage(HttpStatusCode status, string body)
    {
        var detail = TryReadMessage(body);
        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return string.IsNullOrWhiteSpace(detail)
                ? "外置登录失败：用户名或密码错误（也可能是该账号未开通外置登录）。"
                : $"外置登录失败：{detail}";
        }

        if (status == HttpStatusCode.NotFound)
        {
            return "外置登录失败：皮肤站地址不正确（没找到 authserver/authenticate 接口）。";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"外置登录失败：皮肤站返回 HTTP {(int)status}。"
            : $"外置登录失败：{detail}（HTTP {(int)status}）";
    }

    private static string TryReadMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            foreach (var key in new[] { "errorMessage", "message", "error" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text!;
                    }
                }
            }
        }
        catch (Exception)
        {
            // 不是 JSON 就算了
        }

        return string.Empty;
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
