using System.IO.Compression;
using System.Text.RegularExpressions;
using McServerList.Core.Launcher;
using StarLight_Core.Models.Authentication;
using StarLight_Core.Models.Launch;
using StarLight_Core.Utilities;

namespace McServerList.Core.Tests;

/// <summary>
/// 自研启动参数构建的回归测试（替代 StarLight 的 ArgumentsBuildUtil）。
/// 口径：与 StarLight.Core 2.0.5 逐 token 一致，只允许三处刻意差异——
/// ① 不用它的 launch_wrapper.jar；② 品牌换成我们自己的；③ 没替换掉的占位符不再透传给游戏。
/// 最后一条用例直接拿 StarLight 现算一遍做对照，这样口径永远不会"过期"。
/// </summary>
public class GameArgumentsBuilderTests
{
    /// <summary>本机真实游戏目录（不存在就跳过对照用例，CI/别的机器上不会红）。</summary>
    private const string RealGameRoot = @"D:\Minecraft\.minecraft";

    private static AccountProfile Account() => new()
    {
        Kind = AccountKind.离线,
        Name = "Tester",
        Uuid = "11111111222233334444555566667777",
        AccessToken = "0",
    };

    private static Task<GameLaunchArguments> Build(GameArgumentsBuilder builder) =>
        builder.BuildAsync(
            Account(),
            minMemoryMb: 512,
            maxMemoryMb: 4096,
            windowWidth: 854,
            windowHeight: 480,
            fullScreen: false,
            isVersionIsolation: true,
            launcherBrand: "StarLight",
            launcherVersion: "2.0.5",
            versionTypePrefix: "SL");

    [Fact]
    public async Task 新版本跳过带rules的JVM条目并剥掉空格()
    {
        var root = NewGameRoot();
        try
        {
            WriteVersion(root, "X",
                """
                {
                  "id": "X",
                  "type": "release",
                  "mainClass": "net.minecraft.client.main.Main",
                  "assets": "1.20",
                  "arguments": {
                    "jvm": [
                      { "rules": [{ "action": "allow", "os": { "name": "windows" } }], "value": "-XX:ShouldNotAppear=true" },
                      "-Dshould.be.stripped=a b c",
                      "-cp",
                      "${classpath}"
                    ],
                    "game": ["--username", "${auth_player_name}", "--version", "${version_name}"]
                  },
                  "libraries": []
                }
                """);

            using var builder = new GameArgumentsBuilder(root, "X");
            var built = await Build(builder);
            var jvm = built.Segments[1];

            Assert.DoesNotContain("ShouldNotAppear", jvm);            // 带 rules 的条目整条丢掉（StarLight 行为）
            Assert.Contains("-Dshould.be.stripped=abc", jvm);         // 空格被剥掉（StarLight 行为）
            Assert.Contains("-cp", jvm);
            Assert.Contains($"-Dminecraft.client.jar=\"{Path.Combine(root, "versions", "X", "X.jar")}\"", jvm);
            Assert.EndsWith("net.minecraft.client.main.Main", jvm);
            Assert.Equal(512, int.Parse(Regex.Match(built.Segments[0], @"-Xmn(\d+)M").Groups[1].Value));
            Assert.Contains("--version \"X\"", built.Segments[3]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 老版本用默认JVM模板并读minecraftArguments()
    {
        var root = NewGameRoot();
        try
        {
            WriteVersion(root, "old",
                """
                {
                  "id": "old",
                  "type": "release",
                  "mainClass": "net.minecraft.launchwrapper.Launch",
                  "assets": "1.7.10",
                  "minecraftArguments": "--username ${auth_player_name} --gameDir ${game_directory} --userProperties ${user_properties}"
                }
                """);

            using var builder = new GameArgumentsBuilder(root, "old");
            var built = await Build(builder);

            Assert.Contains("-Djava.library.path=", built.Segments[1]);
            Assert.Contains("-Dminecraft.launcher.brand=StarLight", built.Segments[1]);
            Assert.Contains("-cp", built.Segments[1]);
            Assert.DoesNotContain("-Dminecraft.client.jar", built.Segments[1]); // 老版本不加这条（StarLight 行为）
            Assert.Contains("--username Tester", built.Segments[3]);
            Assert.Contains("--userProperties {}", built.Segments[3]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 未解析的占位符连同它的选项一起丢掉()
    {
        var root = NewGameRoot();
        try
        {
            WriteVersion(root, "X",
                """
                {
                  "id": "X",
                  "type": "release",
                  "mainClass": "net.minecraft.client.main.Main",
                  "arguments": {
                    "game": [
                      "--username", "${auth_player_name}",
                      "--clientId", "${clientid}",
                      "--xuid", "${auth_xuid}",
                      "--versionType", "${version_type}"
                    ]
                  }
                }
                """);

            using var builder = new GameArgumentsBuilder(root, "X");
            var built = await Build(builder);
            var game = built.Segments[3];

            Assert.Contains("--username Tester", game);
            Assert.Contains("--versionType \"SL/Release\"", game);
            Assert.DoesNotContain("--clientId", game);
            Assert.DoesNotContain("--xuid", game);
            Assert.DoesNotContain("${", game);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task 继承链合并_客户端jar与资源索引取父版本_库按高版本去重且optifine排最后()
    {
        var root = NewGameRoot();
        try
        {
            WriteVersion(root, "1.20.1",
                """
                {
                  "id": "1.20.1",
                  "type": "release",
                  "mainClass": "net.minecraft.client.main.Main",
                  "assets": "5",
                  "arguments": {
                    "jvm": ["-cp", "${classpath}"],
                    "game": [
                      "--username", "${auth_player_name}",
                      "--version", "${version_name}",
                      "--assetIndex", "${assets_index_name}"
                    ]
                  },
                  "libraries": [
                    { "name": "com.example:shared:1.0" },
                    { "name": "com.example:onlyparent:2.0" }
                  ]
                }
                """);
            WriteVersion(root, "fabric",
                """
                {
                  "id": "fabric",
                  "type": "release",
                  "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
                  "inheritsFrom": "1.20.1",
                  "arguments": {
                    "jvm": ["-cp", "${classpath}"],
                    "game": ["--fabric.only", "yes"]
                  },
                  "libraries": [
                    { "name": "com.example:shared:1.1" },
                    { "name": "optifine:OptiFine:1.20.1_HD_U_I6" }
                  ]
                }
                """);

            using var builder = new GameArgumentsBuilder(root, "fabric");
            var built = await Build(builder);
            var jvm = built.Segments[1];
            var game = built.Segments[3];

            // 客户端 jar / 主类 / 资源索引
            Assert.Equal(Path.Combine(root, "versions", "1.20.1", "1.20.1.jar"), built.ClientJarPath);
            Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", built.MainClass);
            Assert.Contains("--assetIndex 5", game);

            // 游戏参数：自身在前、父版本在后
            Assert.Contains("--fabric.only yes", game);
            Assert.True(game.IndexOf("--fabric.only", StringComparison.Ordinal)
                        < game.IndexOf("--version", StringComparison.Ordinal));

            // classpath：同名库只留高版本、optifine 排最后、客户端 jar 收尾
            Assert.DoesNotContain("shared-1.0.jar", jvm);
            Assert.Contains("shared-1.1.jar", jvm);
            Assert.Contains("onlyparent-2.0.jar", jvm);
            var classpath = Regex.Match(jvm, "-cp \"([^\"]+)\"").Groups[1].Value;
            var entries = classpath.Split(';');
            Assert.Equal(Path.Combine(root, "versions", "1.20.1", "1.20.1.jar"), entries[^1]);
            Assert.Contains("optifine", entries[^2], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void natives只解压当前架构的包且跳过METAINF()
    {
        var root = NewGameRoot();
        try
        {
            var libraries = Path.Combine(root, "libraries");
            WriteJar(Path.Combine(libraries, "org", "lwjgl", "lwjgl", "3.4.1", "lwjgl-3.4.1-natives-windows.jar"),
                "lwjgl.dll", "META-INF/MANIFEST.MF");
            WriteJar(Path.Combine(libraries, "org", "lwjgl", "lwjgl", "3.4.1", "lwjgl-3.4.1-natives-windows-arm64.jar"),
                "lwjgl-arm64.dll");
            WriteVersion(root, "X",
                """
                {
                  "id": "X",
                  "mainClass": "net.minecraft.client.main.Main",
                  "arguments": { "game": [] },
                  "libraries": [
                    { "name": "org.lwjgl:lwjgl:3.4.1:natives-windows" },
                    { "name": "org.lwjgl:lwjgl:3.4.1:natives-windows-arm64" }
                  ]
                }
                """);

            using var builder = new GameArgumentsBuilder(root, "X");
            var natives = builder.ResolveNativesDirectory();
            var extracted = builder.ExtractNatives(natives);

            Assert.Equal(Path.Combine(root, "versions", "X", "X-natives"), natives);
            Assert.Equal(1, extracted);
            Assert.True(File.Exists(Path.Combine(natives, "lwjgl.dll")));
            Assert.False(File.Exists(Path.Combine(natives, "lwjgl-arm64.dll")));
            Assert.False(Directory.Exists(Path.Combine(natives, "META-INF")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Quilt 的官方 profile **连 arguments 字段都没有**（Fabric 至少有空的 game 数组）：
    /// 这时 JVM 模板与游戏参数都必须整个回落到父版本（原版）那一份，否则启动会缺 --username/--gameDir。
    /// </summary>
    [Fact]
    public async Task 子版本既没有arguments也没有minecraftArguments时回落到父版本()
    {
        var root = NewGameRoot();
        try
        {
            WriteVersion(root, "1.21.1",
                """
                {
                  "id": "1.21.1",
                  "type": "release",
                  "mainClass": "net.minecraft.client.main.Main",
                  "assets": "17",
                  "arguments": {
                    "jvm": ["-Djava.library.path=${natives_directory}", "-cp", "${classpath}"],
                    "game": ["--username", "${auth_player_name}", "--gameDir", "${game_directory}", "--assetIndex", "${assets_index_name}"]
                  },
                  "libraries": []
                }
                """);
            // Quilt profile 的形状：只有 id / inheritsFrom / mainClass / libraries
            WriteVersion(root, "quilt",
                """
                {
                  "id": "quilt-loader-0.24.0-1.21.1",
                  "inheritsFrom": "1.21.1",
                  "mainClass": "org.quiltmc.loader.impl.launch.knot.KnotClient",
                  "libraries": []
                }
                """);

            using var builder = new GameArgumentsBuilder(root, "quilt");
            var built = await Build(builder);
            var jvm = built.Segments[1];
            var game = built.Segments[3];

            Assert.Contains("-cp", jvm);                        // 来自父版本的 jvm 参数
            Assert.DoesNotContain("${natives", jvm);            // 占位符已被替换
            Assert.Contains("--username Tester", game);         // 父版本的游戏参数
            Assert.Contains("--gameDir", game);
            Assert.Contains("--assetIndex 17", game);
            Assert.EndsWith("org.quiltmc.loader.impl.launch.knot.KnotClient", jvm);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 拿 StarLight 现算一遍做对照：本机游戏目录里的每个版本逐 token 必须一致
    /// （只归一化三处刻意差异）。注意老版本要排在前面——StarLight 的 JVM 模板是静态字段。
    /// </summary>
    [Fact]
    public async Task 与StarLight逐token一致()
    {
        if (!Directory.Exists(RealGameRoot))
        {
            return; // 本机没有真实游戏目录就跳过
        }

        var versions = new[]
        {
            "1.7.10", "1.12.2", "1.16.4", "1.19.4", "1.20.1", "26.1",
            "1.20.1-fabric-loader_0.19.5", "1.19.4-OptiFine_HD_U_I4",
        };

        foreach (var version in versions)
        {
            if (!File.Exists(Path.Combine(RealGameRoot, "versions", version, version + ".json")))
            {
                continue;
            }

            var core = new GameCoreConfig
            {
                Root = RealGameRoot,
                Version = version,
                IsVersionIsolation = true,
                AuthlibServerUrl = string.Empty,
            };
            var javaConfig = new JavaConfig { JavaPath = "javaw.exe", MaxMemory = 4096, MinMemory = 512 };
            var windowConfig = new GameWindowConfig { Width = 854, Height = 480, IsFullScreen = false };
            BaseAccount account = new OfflineAccount
            {
                Name = "Tester",
                Uuid = "11111111222233334444555566667777",
                AccessToken = "0",
            };
            var starLight = string.Join(" ", await new ArgumentsBuildUtil(windowConfig, core, javaConfig, account).Build());

            using var builder = new GameArgumentsBuilder(RealGameRoot, version);
            var built = await Build(builder);
            var mine = string.Join(" ", built.Segments);

            Assert.Equal(Normalize(starLight, version, built.NativesDirectory), Normalize(mine, version, built.NativesDirectory));
        }
    }

    /// <summary>归一化三处刻意差异：wrapper 段、未解析占位符、natives 目录。</summary>
    private static string Normalize(string arguments, string versionId, string nativesDirectory)
    {
        var normalized = Regex.Replace(arguments, @"-Doolloo\.jlw\.tmpdir=""[^""]*"" -jar ""[^""]*""", string.Empty);
        normalized = Regex.Replace(normalized, @"--[A-Za-z][A-Za-z0-9-]*\s+\$\{[^}]+\}", string.Empty);
        normalized = Regex.Replace(normalized, @"\$\{[^}]+\}", string.Empty);
        normalized = normalized.Replace(
            Path.Combine(RealGameRoot, "versions", versionId, "natives"),
            "{NATIVES}",
            StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(nativesDirectory, "{NATIVES}", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string NewGameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsl-args-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteVersion(string root, string id, string json)
    {
        var directory = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    private static void WriteJar(string path, params string[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("stub");
        }
    }
}
