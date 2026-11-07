using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

public sealed class AccountLoginTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wbl-account-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Microsoft账户可加密保存并恢复()
    {
        var service = new AccountService(_root);
        var profile = new AccountProfile
        {
            Name = "Tester",
            Uuid = "11111111222233334444555566667777",
            AccessToken = "minecraft-access",
            MicrosoftRefreshToken = "refresh-token",
            MicrosoftExpiresAt = DateTimeOffset.Now.AddHours(1),
        };

        service.Save([profile]);
        var restored = Assert.Single(service.Load());

        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal(profile.Uuid, restored.Uuid);
        Assert.Equal(profile.AccessToken, restored.AccessToken);
        Assert.Equal(profile.MicrosoftRefreshToken, restored.MicrosoftRefreshToken);
    }

    [Fact]
    public void 旧账户文件只迁移Microsoft条目并清除旧认证字段()
    {
        Directory.CreateDirectory(_root);
        const string legacyJson = """
            [
              {"Kind":0,"Name":"LegacyLocal","Uuid":"local","AccessToken":"local-token","ClientToken":"client"},
              {"Kind":1,"Name":"LegacyMicrosoft","Uuid":"msa","AccessToken":"msa-token","ClientToken":"","MicrosoftRefreshToken":"refresh"},
              {"Kind":2,"Name":"LegacyExternal","Uuid":"external","AccessToken":"external-token","ServerUrl":"https://example.invalid"},
              {"Kind":3,"Name":"LegacyPortal","Uuid":"portal","AccessToken":"portal-token","UnifiedPassServerId":"server-id"}
            ]
            """;
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(legacyJson), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path.Combine(_root, "accounts.dat"), protectedBytes);

        var service = new AccountService(_root);
        var restored = Assert.Single(service.Load());

        Assert.Equal("LegacyMicrosoft", restored.Name);
        Assert.Equal("msa-token", restored.AccessToken);

        var rewritten = ProtectedData.Unprotect(
            File.ReadAllBytes(Path.Combine(_root, "accounts.dat")), null, DataProtectionScope.CurrentUser);
        var rewrittenJson = Encoding.UTF8.GetString(rewritten);
        Assert.DoesNotContain("LegacyLocal", rewrittenJson);
        Assert.DoesNotContain("LegacyExternal", rewrittenJson);
        Assert.DoesNotContain("LegacyPortal", rewrittenJson);
        Assert.DoesNotContain("\"Kind\"", rewrittenJson);
        Assert.DoesNotContain("\"ClientToken\"", rewrittenJson);
        Assert.DoesNotContain("\"ServerUrl\"", rewrittenJson);
        Assert.DoesNotContain("\"UnifiedPassServerId\"", rewrittenJson);
    }

    [Fact]
    public void 登出会清除本地Microsoft凭据但保留账号条目()
    {
        var service = new AccountService(_root);
        var signedOut = service.SignOut(new AccountProfile
        {
            Name = "Tester",
            Uuid = "11111111222233334444555566667777",
            AccessToken = "minecraft-access",
            MicrosoftRefreshToken = "refresh-token",
        }).Single();

        Assert.True(AccountService.IsSignedOut(signedOut));
        Assert.Empty(signedOut.MicrosoftRefreshToken ?? string.Empty);
        Assert.Null(signedOut.MicrosoftExpiresAt);
        Assert.Equal("Tester", Assert.Single(service.Load()).Name);
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

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup is best effort.
        }
    }
}
