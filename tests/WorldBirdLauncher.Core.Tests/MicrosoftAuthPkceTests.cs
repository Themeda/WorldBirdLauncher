using System.Net;
using System.Net.Http;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

public sealed class MicrosoftAuthPkceTests
{
    [Fact]
    public void PKCE_S256挑战符合RFC示例()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            MicrosoftAuth.CreatePkceChallenge(verifier));
    }

    [Fact]
    public async Task 回环回调交换令牌时携带原始PKCE验证码()
    {
        var handler = new CapturingTokenHandler();
        using var tokenHttp = new HttpClient(handler);
        using var session = MicrosoftAuth.StartAuthorizationCode(
            tokenHttp,
            "https://example.test/token");
        try
        {
            var query = ParseQuery(session.AuthorizationUri.Query);
            Assert.Equal("code", query["response_type"]);
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal(session.RedirectUri.AbsoluteUri, query["redirect_uri"]);
            Assert.Equal(43, query["state"].Length);
            Assert.Equal(43, query["code_challenge"].Length);
            Assert.DoesNotContain("code_verifier", session.AuthorizationUri.Query);

            var tokenTask = session.WaitForTokenAsync();
            var callback = new Uri(
                session.RedirectUri.AbsoluteUri +
                "?code=test-code&state=" +
                Uri.EscapeDataString(query["state"]));
            using var callbackHttp = new HttpClient();
            var response = await callbackHttp.GetAsync(callback);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var token = await tokenTask;
            Assert.Equal("access", token.AccessToken);
            Assert.Equal("refresh", token.RefreshToken);
            var form = ParseForm(handler.RequestBodies.Single());
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("test-code", form["code"]);
            Assert.Equal(session.RedirectUri.AbsoluteUri, form["redirect_uri"]);
            Assert.Equal(43, form["code_verifier"].Length);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task 回调state不一致时立即终止且不交换令牌()
    {
        var handler = new CapturingTokenHandler();
        using var tokenHttp = new HttpClient(handler);
        using var session = MicrosoftAuth.StartAuthorizationCode(
            tokenHttp,
            "https://example.test/token");
        try
        {
            var tokenTask = session.WaitForTokenAsync();
            using var callbackHttp = new HttpClient();
            var response = await callbackHttp.GetAsync(
                new Uri(session.RedirectUri.AbsoluteUri + "?code=test-code&state=wrong"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await Assert.ThrowsAsync<MicrosoftAuthException>(
                async () => await tokenTask);
            Assert.Contains("state 校验失败", error.Message);
            Assert.Empty(handler.RequestBodies);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(
                item => Uri.UnescapeDataString(item[0]),
                item => Uri.UnescapeDataString(item.Length > 1 ? item[1] : string.Empty),
                StringComparer.Ordinal);

    private static Dictionary<string, string> ParseForm(string form) =>
        form.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2))
            .ToDictionary(
                item => Uri.UnescapeDataString(item[0]),
                item => Uri.UnescapeDataString(item.Length > 1 ? item[1] : string.Empty),
                StringComparer.Ordinal);

    private sealed class CapturingTokenHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"access","refresh_token":"refresh","expires_in":3600}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
