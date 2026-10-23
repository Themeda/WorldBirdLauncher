using System.Text.Json;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// Forge 安装的纯逻辑回归（2026-10-06，第二十四轮自研）。
/// 口径来自 Forge 官方安装器（MinecraftForge/Installer）：
/// `json/Artifact.java` 的坐标解析、`json/Util.replaceTokens` 的 token 替换、版本目录名约定。
/// </summary>
public class ForgeInstallerTests
{
    [Theory]
    [InlineData("net.minecraftforge:forge:1.13.2-25.0.53",
        "net/minecraftforge/forge/1.13.2-25.0.53/forge-1.13.2-25.0.53.jar")]
    [InlineData("net.minecraftforge:forge:1.13.2-25.0.53:universal",
        "net/minecraftforge/forge/1.13.2-25.0.53/forge-1.13.2-25.0.53-universal.jar")]
    [InlineData("de.oceanlabs.mcp:mcp_config:1.13.2-20190213.203750@zip",
        "de/oceanlabs/mcp/mcp_config/1.13.2-20190213.203750/mcp_config-1.13.2-20190213.203750.zip")]
    [InlineData("net.minecraft:launchwrapper:1.12",
        "net/minecraft/launchwrapper/1.12/launchwrapper-1.12.jar")]
    public void Maven坐标解析(string descriptor, string expectedRelativePath)
    {
        Assert.True(MavenArtifact.TryParse(descriptor, out var artifact));
        Assert.Equal(expectedRelativePath, artifact.RelativePath);
        Assert.Equal(
            Path.Combine("root", "libraries", expectedRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            artifact.LocalPath("root"));
    }

    [Theory]
    [InlineData("not-a-descriptor")]
    [InlineData("group:artifact")]
    public void 非法的Maven坐标会被拒绝(string descriptor) =>
        Assert.False(MavenArtifact.TryParse(descriptor, out _));

    [Fact]
    public void Forge的token替换与官方一致()
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MAPPINGS"] = @"D:\minecraft\libraries\mcp_config-1.13.2.zip",
            ["SIDE"] = "client",
        };

        // {KEY} 取 data；'字面量' 去引号；\x 转义
        Assert.Equal(@"--input D:\minecraft\libraries\mcp_config-1.13.2.zip",
            ForgeInstallerService.ReplaceTokens(data, "--input {MAPPINGS}"));
        Assert.Equal("client", ForgeInstallerService.ReplaceTokens(data, "{SIDE}"));
        Assert.Equal("abc", ForgeInstallerService.ReplaceTokens(data, "'abc'"));
        Assert.Equal("{SIDE}", ForgeInstallerService.ReplaceTokens(data, @"\{SIDE\}"));

        // 没有的键必须报错（官方安装器也是抛异常）
        Assert.Throws<InvalidOperationException>(() => ForgeInstallerService.ReplaceTokens(data, "{NOPE}"));
        Assert.Throws<InvalidOperationException>(() => ForgeInstallerService.ReplaceTokens(data, "{UNCLOSED"));
    }

    [Fact]
    public void Forge目录名约定()
    {
        Assert.Equal("1.13.2-Forge_25.0.53", ForgeInstallerService.ForgeVersionId("1.13.2", "25.0.53"));
        Assert.Equal("1.7.10-Forge_10.13.4.1614", ForgeInstallerService.ForgeVersionId("1.7.10", "10.13.4.1614"));
    }

    /// <summary>老式安装包（1.7.10）的版本 JSON 来自内嵌 versionInfo，id 要改成我们的目录名。</summary>
    [Fact]
    public void 老式安装包的versionInfo可以直接当版本JSON()
    {
        const string versionInfo = """
            {
              "id": "1.7.10-Forge10.13.4.1614-1.7.10",
              "inheritsFrom": "1.7.10",
              "mainClass": "net.minecraft.launchwrapper.Launch",
              "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory}",
              "libraries": [ { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", "url": "https://maven.minecraftforge.net/" } ]
            }
            """;

        using var document = JsonDocument.Parse(versionInfo);
        var root = document.RootElement;
        Assert.Equal("net.minecraft.launchwrapper.Launch", root.GetProperty("mainClass").GetString());
        Assert.Equal("1.7.10", root.GetProperty("inheritsFrom").GetString());
        Assert.Contains("${game_directory}", root.GetProperty("minecraftArguments").GetString(), StringComparison.Ordinal);

        // 1.7.10 的 maven 版本号带尾巴：<mc>-<forge>-<mc>
        Assert.True(MavenArtifact.TryParse("net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", out var forge));
        Assert.Equal("1.7.10-10.13.4.1614-1.7.10", forge.Version);
        Assert.Equal("net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10.jar",
            forge.RelativePath);
    }
}
