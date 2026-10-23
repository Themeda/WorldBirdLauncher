using System.Text.Json;
using McServerList.Core.Launcher;

namespace McServerList.Core.Tests;

/// <summary>
/// 安装相关的回归测试（2026-10-06，net48 阶段 2：原版 + Fabric 安装自研）。
/// 覆盖：已安装版本列表的扫描规则、Fabric 目录名约定、Fabric 官方档案的改写、下载清单认得 Fabric 的 Maven 风格库。
/// </summary>
public class GameInstallationTests
{
    [Fact]
    public void 已安装版本列表以目录名为准并能标出残缺目录()
    {
        var root = NewGameRoot();
        try
        {
            // 正常版本
            WriteVersion(root, "1.20.1", """{ "id": "1.20.1", "type": "release" }""", withJar: true);

            // JSON 里的 id 与目录名不一致：列表必须显示**目录名**（启动/重命名/校验都按目录名定位）
            WriteVersion(root, "自定义版本", """{ "id": "another-id", "type": null }""", withJar: true);

            // 只有 jar、没有同名 JSON 的残缺目录
            Directory.CreateDirectory(Path.Combine(root, "versions", "残缺"));
            File.WriteAllText(Path.Combine(root, "versions", "残缺", "残缺.jar"), "stub");

            // 空目录：不该出现在列表里
            Directory.CreateDirectory(Path.Combine(root, "versions", "空目录"));

            var versions = new GameInstallationService().GetInstalledVersions(root);

            var normal = Assert.Single(versions, v => v.Id == "1.20.1");
            Assert.Equal("1.20.1（release）", normal.DisplayName);
            Assert.True(normal.IsUsable);

            var custom = Assert.Single(versions, v => v.Id == "自定义版本");
            Assert.Equal("自定义版本", custom.DisplayName);

            var broken = Assert.Single(versions, v => v.Id == "残缺");
            Assert.False(broken.IsUsable);
            Assert.Contains("缺少版本 JSON", broken.DisplayName, StringComparison.Ordinal);

            Assert.DoesNotContain(versions, v => v.Id == "空目录");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fabric目录名沿用历史约定()
    {
        Assert.Equal("1.20.1-fabric-loader_0.19.5", GameInstallationService.FabricVersionId("1.20.1", "0.19.5"));
        Assert.Equal("1.16.4-fabric-loader_0.19.4", GameInstallationService.FabricVersionId("1.16.4", "0.19.4"));
    }

    [Fact]
    public void 改写Fabric官方档案只改id()
    {
        // 与 meta.fabricmc.net 真实返回同形状
        const string profile = """
            {
              "id": "fabric-loader-0.19.5-1.21.1",
              "inheritsFrom": "1.21.1",
              "releaseTime": "2024-08-08T14:19:28+0000",
              "time": "2024-08-08T14:19:28+0000",
              "type": "release",
              "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
              "arguments": { "game": [], "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "] },
              "libraries": [
                { "name": "org.ow2.asm:asm:9.10.1", "url": "https://maven.fabricmc.net/", "size": 126151 },
                { "name": "net.fabricmc:fabric-loader:0.19.5", "url": "https://maven.fabricmc.net/" }
              ]
            }
            """;

        var rewritten = GameInstallationService.RewriteFabricProfileJson(profile, "1.21.1-fabric-loader_0.19.5");
        using var document = JsonDocument.Parse(rewritten);
        var root = document.RootElement;

        Assert.Equal("1.21.1-fabric-loader_0.19.5", root.GetProperty("id").GetString());
        Assert.Equal("1.21.1", root.GetProperty("inheritsFrom").GetString());       // 必须继续指向原版
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", root.GetProperty("mainClass").GetString());
        Assert.Equal(2, root.GetProperty("libraries").GetArrayLength());
        Assert.Equal("-DFabricMcEmu= net.minecraft.client.main.Main ",
            root.GetProperty("arguments").GetProperty("jvm")[0].GetString());
    }

    [Fact]
    public void 下载清单认得Fabric的Maven风格库()
    {
        var root = NewGameRoot();
        try
        {
            var versionId = "1.21.1-fabric-loader_0.19.5";
            var jsonPath = WriteVersion(root, versionId,
                """
                {
                  "id": "1.21.1-fabric-loader_0.19.5",
                  "inheritsFrom": "1.21.1",
                  "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
                  "libraries": [
                    { "name": "org.ow2.asm:asm:9.10.1", "url": "https://maven.fabricmc.net/", "size": 126151 },
                    { "name": "net.fabricmc:fabric-loader:0.19.5", "url": "https://maven.fabricmc.net/" }
                  ]
                }
                """,
                withJar: false);

            var planned = GameFileDownloader.BuildPlan(root, versionId, jsonPath, out var assetIndexId);

            Assert.Null(assetIndexId);
            Assert.Equal(2, planned.Count);

            var asm = Assert.Single(planned, f => f.Path.EndsWith(Path.Combine("org", "ow2", "asm", "asm", "9.10.1", "asm-9.10.1.jar"),
                StringComparison.OrdinalIgnoreCase));
            Assert.Equal("https://maven.fabricmc.net/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar", asm.OfficialUrl);
            Assert.Equal("https://bmclapi2.bangbang93.com/maven/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar", asm.MirrorUrl);
            Assert.Equal(126151, asm.Size);

            // 没有 size 的库（intermediary / loader）：Size=0 表示"只判断存在性"
            var loader = Assert.Single(planned, f => f.Path.EndsWith("fabric-loader-0.19.5.jar", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, loader.Size);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 2026-10-06 装 1.7.10-Forge 时踩的两个坑：
    /// ① 老版本 JSON 里的 maven 库常常不写 url（guava 等 6 个），必须回落到 libraries.minecraft.net，否则下不到；
    /// ② 反过来，`downloads` 节点存在但 artifact 的 path/url 全空的是 Mojang 早就下架的包
    ///    （1.7.10 的 tv.twitch:twitch-platform 就是），要跳过，绝不能拿 maven 坐标拼一个必然 404 的地址。
    /// </summary>
    [Fact]
    public void 下载清单对老的Maven库回落到官方资源站_对已下架的包跳过()
    {
        var root = NewGameRoot();
        try
        {
            var versionId = "1.7.10-Forge_10.13.4.1614";
            var jsonPath = WriteVersion(root, versionId,
                """
                {
                  "id": "1.7.10-Forge_10.13.4.1614",
                  "inheritsFrom": "1.7.10",
                  "libraries": [
                    { "name": "com.google.guava:guava:17.0" },
                    { "name": "net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10", "url": "https://maven.minecraftforge.net/" },
                    { "name": "tv.twitch:twitch-platform:5.16", "downloads": { "artifact": { "path": "", "url": "", "size": 0, "sha1": "" } } }
                  ]
                }
                """,
                withJar: false);

            var planned = GameFileDownloader.BuildPlan(root, versionId, jsonPath, out _);

            var guava = Assert.Single(planned, f => f.Path.EndsWith("guava-17.0.jar", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("https://libraries.minecraft.net/com/google/guava/guava/17.0/guava-17.0.jar", guava.OfficialUrl);
            Assert.Equal("https://bmclapi2.bangbang93.com/maven/com/google/guava/guava/17.0/guava-17.0.jar", guava.MirrorUrl);

            var forge = Assert.Single(planned, f => f.Path.EndsWith("forge-1.7.10-10.13.4.1614-1.7.10.jar", StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("https://maven.minecraftforge.net/", forge.OfficialUrl, StringComparison.Ordinal);

            Assert.DoesNotContain(planned, f => f.Path.Contains("twitch-platform", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsl-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteVersion(string root, string id, string json, bool withJar)
    {
        var directory = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(directory);
        var jsonPath = Path.Combine(directory, id + ".json");
        File.WriteAllText(jsonPath, json);
        if (withJar)
        {
            File.WriteAllText(Path.Combine(directory, id + ".jar"), "stub");
        }

        return jsonPath;
    }
}
