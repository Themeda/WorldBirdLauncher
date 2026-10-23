using System.Text.Json;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 版本 JSON 形状相关的回归测试。
///
/// 背景（2026-10-06 第十一轮实测踩到的两个坑）：
/// 1) 1.13+ 的 <c>arguments.game/jvm</c> 元素不一定是字符串，带 rules 的对象也要原样保留，
///    否则 OptiFine 独立版本 JSON 生成时直接抛 InvalidOperationException；
/// 2) 资源索引在版本 JSON 的**顶层** <c>assetIndex</c>（不在 <c>downloads</c> 里），
///    只认 downloads 会导致资源索引与全部资源对象都下不下来，装完的游戏没有贴图/声音。
/// </summary>
public class LauncherJsonShapeTests
{
    [Fact]
    public void 合并1_13以上参数时保留带rules的对象()
    {
        using var vanillaDocument = JsonDocument.Parse(
            """
            {
              "game": [
                "--username", "${auth_player_name}",
                { "rules": [{ "action": "allow", "features": { "is_demo_user": true } }], "value": "--demo" }
              ],
              "jvm": [
                { "rules": [{ "action": "allow", "os": { "name": "windows" } }], "value": "-Dos.name=Windows 10" },
                "-Djava.library.path=${natives_directory}"
              ]
            }
            """);
        using var optifineDocument = JsonDocument.Parse(
            """
            { "arguments": { "game": ["--tweakClass", "optifine.OptiFineTweaker"] } }
            """);

        var game = OptiFineInstallerService.MergeArguments(
            vanillaDocument.RootElement, optifineDocument.RootElement, "game");
        var jvm = OptiFineInstallerService.MergeArguments(
            vanillaDocument.RootElement, optifineDocument.RootElement, "jvm");

        // 对象参数被原样保留（不是空字符串、也不抛异常）
        Assert.Contains(game, a => a.Contains("\"is_demo_user\"", StringComparison.Ordinal));
        Assert.Contains(jvm, a => a.Contains("Windows 10", StringComparison.Ordinal));

        // OptiFine 新增的 tweakClass 追加在后面（每一段都是"原始 JSON 文本"，字符串元素含引号）
        Assert.Equal("\"--tweakClass\"", game[^2]);
        Assert.Equal("\"optifine.OptiFineTweaker\"", game[^1]);

        // 合并后的每一段都必须是合法 JSON，否则写出来的版本 JSON 是坏的
        foreach (var argument in game.Concat(jvm))
        {
            using var _ = JsonDocument.Parse(argument);
        }

        Assert.Equal(5, game.Count);
        Assert.Equal(2, jvm.Count);
    }

    [Fact]
    public void 合并参数时对重复项去重()
    {
        using var vanillaDocument = JsonDocument.Parse("""{ "game": ["--username", "${auth_player_name}", "--username"] }""");
        using var optifineDocument = JsonDocument.Parse("""{ "arguments": { "game": ["--username"] } }""");

        var game = OptiFineInstallerService.MergeArguments(
            vanillaDocument.RootElement, optifineDocument.RootElement, "game");

        Assert.Equal(["\"--username\"", "\"${auth_player_name}\""], game);
    }

    [Fact]
    public void 规划下载清单时从顶层读取资源索引()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "versions", "X"));
        var versionJsonPath = Path.Combine(root, "versions", "X", "X.json");
        File.WriteAllText(versionJsonPath,
            """
            {
              "id": "X",
              "assetIndex": { "id": "3", "size": 411679, "url": "https://piston-meta.mojang.com/v1/packages/x/3.json" },
              "downloads": { "client": { "size": 10, "url": "https://piston-data.mojang.com/v1/objects/x/client.jar" } },
              "libraries": []
            }
            """);

        try
        {
            var planned = GameFileDownloader.BuildPlan(root, "X", versionJsonPath, out var assetIndexId);

            Assert.Equal("3", assetIndexId);
            Assert.Contains(planned, f => f.Path.EndsWith(Path.Combine("assets", "indexes", "3.json"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(planned, f => f.Path.EndsWith("X.jar", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>1.16.x/1.17.x 官方 JSON 的"同名两条"：只有带 natives 的那条才提供 dll。</summary>
    private const string DuplicateStyleLibraries =
        """
        {
          "libraries": [
            {
              "name": "org.lwjgl:lwjgl:3.2.2",
              "downloads": { "artifact": {
                  "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar", "sha1": "aa", "size": 1,
                  "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar" } },
              "rules": [{ "action": "allow" }]
            },
            {
              "name": "org.lwjgl:lwjgl:3.2.2",
              "downloads": {
                "artifact": {
                  "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar", "sha1": "aa", "size": 1,
                  "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar" },
                "classifiers": {
                  "natives-windows": {
                    "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2-natives-windows.jar", "sha1": "bb", "size": 2,
                    "url": "https://libraries.minecraft.net/org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2-natives-windows.jar" }
                }
              },
              "natives": { "windows": "natives-windows" },
              "rules": [{ "action": "allow" }]
            }
          ]
        }
        """;

    [Fact]
    public void 下载清单会带上本机natives包()
    {
        using var document = JsonDocument.Parse(DuplicateStyleLibraries);
        var libraries = document.RootElement.GetProperty("libraries").EnumerateArray().ToList();

        var plain = MinecraftLibraryRules.ResolveFiles(libraries[0]).ToList();
        var withNatives = MinecraftLibraryRules.ResolveFiles(libraries[1]).ToList();

        Assert.Single(plain);
        Assert.EndsWith("lwjgl-3.2.2.jar", plain[0].Path);

        Assert.Equal(2, withNatives.Count);
        Assert.Contains(withNatives, f => f.Path.EndsWith("lwjgl-3.2.2-natives-windows.jar", StringComparison.Ordinal));
    }

    [Fact]
    public void 只有带natives映射的库才会被解压()
    {
        using var document = JsonDocument.Parse(DuplicateStyleLibraries);
        var libraries = document.RootElement.GetProperty("libraries").EnumerateArray().ToList();

        Assert.Null(MinecraftLibraryRules.ResolveNatives(libraries[0]));

        var natives = MinecraftLibraryRules.ResolveNatives(libraries[1]);
        Assert.NotNull(natives);
        Assert.EndsWith("lwjgl-3.2.2-natives-windows.jar", natives!.Value.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void natives目录从巨型参数里解析而不是只匹配开头()
    {
        var request = new GameLaunchRequest
        {
            GameRoot = @"D:\mc",
            VersionId = "1.16.4-fabric-loader_0.19.4",
            JavaPath = "javaw.exe",
            Account = new AccountProfile { Kind = AccountKind.离线, Name = "Probe" },
        };

        // StarLight 会把一堆 JVM 参数拼成一个超长参数项，这里模拟那种形状
        var blob = "-Dminecraft.client.jar=\"D:\\mc\\versions\\1.16.4\\1.16.4.jar\" "
                   + "-Djava.library.path=\"D:\\mc\\versions\\1.16.4-fabric-loader_0.19.4\\natives\" "
                   + "-Dminecraft.launcher.brand=StarLight";
        Assert.Equal(
            @"D:\mc\versions\1.16.4-fabric-loader_0.19.4\natives",
            GameLaunchService.ResolveNativesDirectory(request, [blob]));

        // 取不到参数时退回官方目录规则
        Assert.Equal(
            @"D:\mc\versions\1.16.4-fabric-loader_0.19.4\1.16.4-fabric-loader_0.19.4-natives",
            GameLaunchService.ResolveNativesDirectory(request, ["-Dfoo=bar"]));
    }
}
