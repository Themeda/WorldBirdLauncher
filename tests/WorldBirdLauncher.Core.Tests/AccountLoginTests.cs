using System.Net;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 登录相关的纯逻辑回归（2026-10-06 第二十五轮：离线 + 统一通行证自研，Core 彻底不依赖 StarLight）。
/// 统一通行证的接口是 Yggdrasil 那一套：<c>POST {站点}/&lt;服务器ID&gt;/authserver/authenticate</c>。
/// </summary>
public class AccountLoginTests
{
    [Theory]
    [InlineData("https://auth.mc-user.com:233/")]
    [InlineData("https://auth.mc-user.com:233")]          // 少个斜杠
    [InlineData("auth.mc-user.com:233")]                  // 没写协议
    public void 统一通行证登录地址会被补齐(string baseUrl) =>
        Assert.Equal(
            "https://auth.mc-user.com:233/SERVER/authserver/authenticate",
            UnifiedPassAuth.BuildAuthenticateUrl(baseUrl, "SERVER"));

    [Fact]
    public void 统一通行证请求体形状与Yggdrasil一致()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            UnifiedPassAuth.BuildRequestBody("JarnoHawk", "secret"));
        var root = document.RootElement;

        Assert.Equal("WorldBirdLauncher", root.GetProperty("agent").GetProperty("name").GetString());
        Assert.Equal("JarnoHawk", root.GetProperty("username").GetString());
        Assert.Equal("secret", root.GetProperty("password").GetString());
        Assert.True(root.GetProperty("requestUser").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("clientToken").ValueKind);
    }

    [Fact]
    public void 统一通行证成功响应按selectedProfile的id取UUID()
    {
        // 真实响应形状（StarLight 的模型读的是 uuid，所以那条路一直是空的）
        const string json = """
            {
              "accessToken": "token-123",
              "clientToken": "client-456",
              "availableProfiles": [ { "id": "11111111222233334444555566667777", "name": "JarnoHawk" } ],
              "selectedProfile": { "id": "11111111222233334444555566667777", "name": "JarnoHawk" },
              "user": { "id": "abcdef" }
            }
            """;

        var profile = UnifiedPassAuth.ParseResponse(json, "4031fd748f9f11ea908800163e095b49");

        Assert.Equal(AccountKind.统一通行证, profile.Kind);
        Assert.Equal("JarnoHawk", profile.Name);
        Assert.Equal("11111111222233334444555566667777", profile.Uuid);
        Assert.Equal("token-123", profile.AccessToken);
        Assert.Equal("client-456", profile.ClientToken);
        Assert.Equal("4031fd748f9f11ea908800163e095b49", profile.UnifiedPassServerId);
    }

    [Fact]
    public void 统一通行证失败文案优先用服务端的errorMessage()
    {
        Assert.Contains("密码错误", UnifiedPassAuth.TranslateError(
            HttpStatusCode.Forbidden, """{"error":"ForbiddenOperationException","errorMessage":"密码错误"}"""));
        Assert.Contains("账号或密码", UnifiedPassAuth.TranslateError(HttpStatusCode.Forbidden, "not-json"));
        Assert.Contains("服务器 ID", UnifiedPassAuth.TranslateError(HttpStatusCode.NotFound, string.Empty));
    }

    [Fact]
    public void 离线UUID是官方值()
    {
        // 官方规则：MD5("OfflinePlayer:" + 名字)，大端十六进制拼成带横线的 UUID
        Assert.Equal("b50ad385-829d-3141-a216-7e7d7539ba7f", AccountService.OfflineUuid("Notch"));
        Assert.Equal(AccountService.OfflineUuid("Tester"), AccountService.OfflineUuid("Tester")); // 同名同 UUID
        Assert.NotEqual(AccountService.OfflineUuid("Tester"), AccountService.OfflineUuid("tester"));
    }

    [Fact]
    public void 离线登录产出可用的账户档案()
    {
        var profile = new AccountService().LoginOffline(" Tester ");

        Assert.Equal(AccountKind.离线, profile.Kind);
        Assert.Equal("Tester", profile.Name);
        Assert.Equal(AccountService.OfflineUuid("Tester"), profile.Uuid);
        Assert.False(string.IsNullOrWhiteSpace(profile.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(profile.ClientToken));
    }

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.20.4", "20.4.")]
    public void NeoForge版本前缀映射(string mcVersion, string expected) =>
        Assert.Equal(expected, GameInstallationService.NeoForgeVersionPrefix(mcVersion));

    [Fact]
    public void 加载器目录名约定()
    {
        Assert.Equal("1.21.1-neoforge_21.1.255",
            ForgeInstallerService.VersionId(ForgeInstallerService.LoaderKind.NeoForge, "1.21.1", "21.1.255"));
        Assert.Equal("1.21.1-Forge_25.0.53",
            ForgeInstallerService.VersionId(ForgeInstallerService.LoaderKind.Forge, "1.21.1", "25.0.53"));
        Assert.Equal("1.21.1-quilt-loader_0.24.0", GameInstallationService.QuiltVersionId("1.21.1", "0.24.0"));
        Assert.Equal("1.21.1-fabric-loader_0.19.5", GameInstallationService.FabricVersionId("1.21.1", "0.19.5"));
    }
}
