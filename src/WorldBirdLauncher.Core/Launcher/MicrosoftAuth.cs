using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// Microsoft authentication: authorization code + PKCE + loopback redirect →
/// Xbox Live → XSTS → Minecraft Services → profile.
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
    private const string AuthorizeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string CallbackPath = "/callback";
    private const int MaximumCallbackBytes = 16 * 1024;
    private const string XblUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string McLoginUrl = "https://api.minecraftservices.com/authentication/login_with_xbox";
    private const string McProfileUrl = "https://api.minecraftservices.com/minecraft/profile";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "WorldBirdLauncher/0.2.0 (+https://github.com/Themeda/WorldBirdLauncher)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>微软 OAuth token（refresh_token 用于下次免授权续期）。</summary>
    public sealed record MicrosoftToken(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

    /// <summary>
    /// 启动一次性回环监听并生成授权 URL。随机端口由系统分配，state 与 code_verifier
    /// 只保存在当前对象的私有字段中。
    /// </summary>
    public static AuthorizationSession StartAuthorizationCode() =>
        StartAuthorizationCode(Http, TokenUrl);

    internal static AuthorizationSession StartAuthorizationCode(
        HttpClient httpClient,
        string tokenEndpoint)
    {
        var verifier = Base64Url(RandomBytes(32));
        var state = Base64Url(RandomBytes(32));
        var challenge = CreatePkceChallenge(verifier);
        var listeners = StartLoopbackListeners(out var port);
        var redirectUri = $"http://localhost:{port}{CallbackPath}";
        var query = string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = Scope,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        }.Select(item =>
            Uri.EscapeDataString(item.Key) + "=" + Uri.EscapeDataString(item.Value)));
        return new AuthorizationSession(
            new Uri(AuthorizeUrl + "?" + query),
            new Uri(redirectUri),
            state,
            verifier,
            listeners,
            httpClient,
            tokenEndpoint);
    }

    public sealed class AuthorizationSession : IDisposable
    {
        private readonly Uri _redirectUri;
        private readonly string _state;
        private readonly string _verifier;
        private readonly List<TcpListener> _listeners;
        private readonly HttpClient _httpClient;
        private readonly string _tokenEndpoint;
        private int _started;
        private int _disposed;

        internal AuthorizationSession(
            Uri authorizationUri,
            Uri redirectUri,
            string state,
            string verifier,
            List<TcpListener> listeners,
            HttpClient httpClient,
            string tokenEndpoint)
        {
            AuthorizationUri = authorizationUri;
            _redirectUri = redirectUri;
            _state = state;
            _verifier = verifier;
            _listeners = listeners;
            _httpClient = httpClient;
            _tokenEndpoint = tokenEndpoint;
        }

        public Uri AuthorizationUri { get; }

        public Uri RedirectUri => _redirectUri;

        public async Task<MicrosoftToken> WaitForTokenAsync(
            IProgress<string>? status = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("本次微软授权回调已经处理。");
            }
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(AuthorizationSession));
            }

            status?.Report("等待浏览器完成微软授权…");
            var callback = await ReceiveCallbackAsync(cancellationToken).ConfigureAwait(false);
            if (!FixedTimeEquals(callback.State, _state))
            {
                throw new MicrosoftAuthException(
                    "微软授权回调 state 校验失败。请关闭其他网页后重新开始登录，并且只保留微软授权页面。");
            }
            if (!string.IsNullOrWhiteSpace(callback.Error))
            {
                var detail = string.IsNullOrWhiteSpace(callback.ErrorDescription)
                    ? callback.Error
                    : callback.Error + "：" + callback.ErrorDescription;
                throw new MicrosoftAuthException("微软授权失败：" + detail);
            }
            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new MicrosoftAuthException("微软授权回调缺少 code，请重新登录。");
            }

            status?.Report("浏览器授权完成，正在交换微软令牌…");
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "authorization_code",
                ["code"] = callback.Code!,
                ["redirect_uri"] = _redirectUri.AbsoluteUri,
                ["code_verifier"] = _verifier,
                ["scope"] = Scope,
            });
            using var response = await _httpClient.PostAsync(
                _tokenEndpoint,
                content,
                cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = TryParse(body) ??
                                 throw new MicrosoftAuthException("微软令牌响应不是合法 JSON，请稍后重试。");
            var root = document.RootElement;
            var accessToken = GetString(root, "access_token");
            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(accessToken))
            {
                throw new MicrosoftAuthException(
                    "微软令牌交换失败：" + ExtractOAuthError(body));
            }

            return new MicrosoftToken(
                accessToken!,
                GetString(root, "refresh_token"),
                GetInt(root, "expires_in", 3600));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                StopListeners();
            }
        }

        private void StopListeners()
        {
            foreach (var listener in _listeners)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }
        }

        private async Task<CallbackResult> ReceiveCallbackAsync(
            CancellationToken cancellationToken)
        {
            var accepts = _listeners
                .Select(listener => listener.AcceptTcpClientAsync())
                .ToArray();
            using var registration = cancellationToken.Register(StopListeners);
            TcpClient client;
            try
            {
                var completed = await Task.WhenAny(accepts).ConfigureAwait(false);
                foreach (var pending in accepts.Where(task => !ReferenceEquals(task, completed)))
                {
                    _ = pending.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                client = await completed.ConfigureAwait(false);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                StopListeners();
            }

            using (client)
            using (var interrupt = cancellationToken.Register(client.Close))
            {
                var request = await ReadRequestAsync(
                    client.GetStream(),
                    cancellationToken).ConfigureAwait(false);
                var result = ParseCallback(request.Target);
                var validState = FixedTimeEquals(result.State, _state);
                var statusCode = validState && result.Error.Length == 0 && result.Code.Length > 0
                    ? 200
                    : 400;
                var title = statusCode == 200
                    ? "授权完成，请返回 WorldBirdLauncher"
                    : "授权失败，请返回 WorldBirdLauncher 查看原因";
                await WriteResponseAsync(
                    client.GetStream(),
                    statusCode,
                    title,
                    cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
    }

    private sealed record CallbackResult(
        string Code,
        string State,
        string Error,
        string ErrorDescription);

    private sealed record HttpRequestData(string Target);

    private static List<TcpListener> StartLoopbackListeners(out int port)
    {
        var listeners = new List<TcpListener>(2);
        var primary = new TcpListener(IPAddress.Loopback, 0);
        primary.Start();
        listeners.Add(primary);
        port = ((IPEndPoint)primary.LocalEndpoint).Port;
        try
        {
            var secondary = new TcpListener(IPAddress.IPv6Loopback, port);
            secondary.Start();
            listeners.Add(secondary);
        }
        catch (SocketException)
        {
            // IPv4 回环已经可用；浏览器探测 localhost 时会回退到 IPv4。
        }
        return listeners;
    }

    private static async Task<HttpRequestData> ReadRequestAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[2048];
        using var received = new MemoryStream();
        while (received.Length < MaximumCallbackBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }
            received.Write(buffer, 0, read);
            var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
            {
                continue;
            }
            var requestLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
            var parts = requestLine.Split(' ');
            if (parts.Length < 3 ||
                !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                throw new MicrosoftAuthException("微软授权回调不是有效的 GET 请求。");
            }
            var target = parts[1];
            var pathEnd = target.IndexOf('?');
            var path = pathEnd < 0 ? target : target[..pathEnd];
            if (!string.Equals(path, CallbackPath, StringComparison.Ordinal))
            {
                throw new MicrosoftAuthException("微软授权回调路径无效。");
            }
            return new HttpRequestData(target);
        }
        throw new MicrosoftAuthException("微软授权回调请求过大或格式无效。");
    }

    private static CallbackResult ParseCallback(string target)
    {
        var queryStart = target.IndexOf('?');
        if (queryStart < 0)
        {
            return new CallbackResult(string.Empty, string.Empty, string.Empty, string.Empty);
        }
        var parameters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in target[(queryStart + 1)..].Split('&'))
        {
            if (pair.Length == 0)
            {
                continue;
            }
            var separator = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            if (!parameters.TryGetValue(key, out var values))
            {
                values = [];
                parameters[key] = values;
            }
            values.Add(value);
        }

        string Single(string name) =>
            parameters.TryGetValue(name, out var values) && values.Count == 1
                ? values[0]
                : string.Empty;

        return new CallbackResult(
            Single("code"),
            Single("state"),
            Single("error"),
            Single("error_description"));
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int statusCode,
        string title,
        CancellationToken cancellationToken)
    {
        var html =
            "<!doctype html><meta charset=\"utf-8\"><title>" + title + "</title>" +
            "<style>body{font-family:'Microsoft YaHei UI','Segoe UI',sans-serif;" +
            "background:#fff8fb;color:#28303d;padding:48px;text-align:center}" +
            "main{max-width:520px;margin:auto;padding:28px;border:1px solid #ebd4de;" +
            "border-radius:12px;background:#fff}h1{font-size:22px}</style>" +
            "<main><h1>" + title + "</h1><p>现在可以关闭此浏览器页面。</p></main>";
        var body = Encoding.UTF8.GetBytes(html);
        var reason = statusCode == 200 ? "OK" : "Bad Request";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            "Cache-Control: no-store\r\n" +
            "Pragma: no-cache\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
        await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return bytes;
    }

    private static byte[] Sha256(byte[] value)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(value);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    internal static string CreatePkceChallenge(string verifier) =>
        Base64Url(Sha256(Encoding.ASCII.GetBytes(verifier)));

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.ASCII.GetBytes(right ?? string.Empty);
        var difference = leftBytes.Length ^ rightBytes.Length;
        var length = Math.Max(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < length; index++)
        {
            var leftValue = index < leftBytes.Length ? leftBytes[index] : 0;
            var rightValue = index < rightBytes.Length ? rightBytes[index] : 0;
            difference |= leftValue ^ rightValue;
        }
        return difference == 0;
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
                    "（403 Invalid app registration）。审核通过前无法完成正版登录，请稍后重试。");
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
            Name = name!,
            Uuid = uuid!,
            AccessToken = mcAccess!,
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
