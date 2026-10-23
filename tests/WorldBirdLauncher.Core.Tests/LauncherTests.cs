using System.IO.Compression;
using System.Text;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

public class JavaVersionMappingTests
{
    [Theory]
    [InlineData("1.12.2", 8)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 17)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.20.1", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.4", 21)]
    [InlineData("26.1", 21)]
    [InlineData("", 21)]
    public void 按游戏版本推荐Java主版本(string minecraftVersion, int expected) =>
        Assert.Equal(expected, JavaRuntimeService.RecommendFeatureVersion(minecraftVersion));
}

public class GameLaunchServiceTests
{
    [Theory]
    [InlineData("play.example.com", "play.example.com", 25565)]
    [InlineData("play.example.com:25566", "play.example.com", 25566)]
    [InlineData("1.2.3.4:25565", "1.2.3.4", 25565)]
    [InlineData("  1.2.3.4:1234  ", "1.2.3.4", 1234)]
    public void 服务器地址能正确拆分(string input, string host, int port)
    {
        var (actualHost, actualPort) = GameLaunchService.SplitServerAddress(input);
        Assert.Equal(host, actualHost);
        Assert.Equal(port, actualPort);
    }

    [Fact]
    public void 未配置服务器时不注入进服参数() =>
        Assert.Empty(GameLaunchService.BuildServerArguments(null));

    [Theory]
    [InlineData("play.example.com", "play.example.com:25565")]
    [InlineData("play.example.com:25566", "play.example.com:25566")]
    [InlineData("  1.2.3.4:1234  ", "1.2.3.4:1234")]
    public void 进服参数使用quickPlay语法(string input, string expected)
    {
        // 现代 Minecraft 已移除 --server / --port（26.1 实测会 "Completely ignored"），
        // 必须用 --quickPlayMultiplayer 主机:端口
        var arguments = GameLaunchService.BuildServerArguments(input);

        Assert.Equal(["--quickPlayMultiplayer", expected], arguments);
        Assert.DoesNotContain("--server", arguments);
    }

    [Theory]
    [InlineData("1.21.1")]
    [InlineData("1.20.1-fabric-loader_0.19.5")]
    [InlineData("26.1")]
    public void 新版用quickPlay进服(string versionId)
    {
        // 1.20 起才有 --quickPlayMultiplayer
        var arguments = GameLaunchService.BuildServerArguments("play.example.com:25566", versionId);

        Assert.Equal(["--quickPlayMultiplayer", "play.example.com:25566"], arguments);
    }

    [Theory]
    [InlineData("1.19.4", "play.example.com", 25565)]
    [InlineData("1.19.4-OptiFine_HD_U_I4", "play.example.com", 25566)]
    [InlineData("1.12.2-Forge_14.23.5.2859", "1.2.3.4", 1234)]
    [InlineData("1.7.10", "legacy.example.com", 25565)]
    public void 老版本用server_port进服(string versionId, string host, int port)
    {
        // 老版本不认 --quickPlayMultiplayer（会被静默忽略，表现为"点了启动没进服"），必须用 --server/--port
        var arguments = GameLaunchService.BuildServerArguments($"{host}:{port}", versionId);

        Assert.Equal(["--server", host, "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], arguments);
        Assert.DoesNotContain("--quickPlayMultiplayer", arguments);
    }
}

public class ModpackInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mcsl-tests-" + Guid.NewGuid().ToString("N"));

    public ModpackInstallerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 识别CurseForge整合包()
    {
        var path = BuildZip("cf.zip", new Dictionary<string, string>
        {
            ["manifest.json"] = """
            {
              "name": "测试整合包",
              "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "forge-47.2.0", "primary": true } ] },
              "overrides": "overrides",
              "files": [ { "projectID": 1, "fileID": 2, "required": true }, { "projectID": 3, "fileID": 4, "required": true } ]
            }
            """,
            ["overrides/config/test.toml"] = "a = 1",
        });

        var info = new ModpackInstaller(new GameInstallationService()).Read(path);

        Assert.Equal(ModpackFormat.CurseForge, info.Format);
        Assert.Equal("测试整合包", info.Name);
        Assert.Equal("1.20.1", info.MinecraftVersion);
        Assert.Equal("Forge", info.Loader);
        Assert.Equal("47.2.0", info.LoaderVersion);
        Assert.Equal(2, info.RemoteFiles.Count);
        Assert.Equal(2, info.UnresolvedFileCount);
        Assert.Single(info.ArchiveFiles);
    }

    [Fact]
    public void 识别Modrinth整合包()
    {
        var path = BuildZip("modrinth.zip", new Dictionary<string, string>
        {
            ["modrinth.index.json"] = """
            {
              "name": "Modrinth 包",
              "dependencies": { "minecraft": "1.21.1", "fabric-loader": "0.16.0" },
              "files": [
                { "path": "mods/a.jar", "downloads": [ "https://example.com/a.jar" ], "fileSize": 100,
                  "hashes": { "sha1": "abc" } }
              ]
            }
            """,
            ["overrides/config/b.json"] = "{}",
        });

        var info = new ModpackInstaller(new GameInstallationService()).Read(path);

        Assert.Equal(ModpackFormat.Modrinth, info.Format);
        Assert.Equal("1.21.1", info.MinecraftVersion);
        Assert.Equal("Fabric", info.Loader);
        Assert.Equal("0.16.0", info.LoaderVersion);
        Assert.Single(info.RemoteFiles);
        Assert.Equal("https://example.com/a.jar", info.RemoteFiles[0].DownloadUrl);
        Assert.Equal(0, info.UnresolvedFileCount);
        Assert.Single(info.ArchiveFiles);
    }

    [Fact]
    public void 识别离线完整包并推断版本号()
    {
        var path = BuildZip("offline.zip", new Dictionary<string, string>
        {
            ["versions/1.20.4/1.20.4.json"] = "{}",
            ["mods/example.jar"] = "binary",
            [".minecraft/config/options.txt"] = "lang:zh_cn",
        });

        var info = new ModpackInstaller(new GameInstallationService()).Read(path);

        Assert.Equal(ModpackFormat.OfflineArchive, info.Format);
        Assert.Equal("1.20.4", info.MinecraftVersion);
        Assert.Equal(3, info.ArchiveFiles.Count);
        Assert.Empty(info.RemoteFiles);
    }

    [Fact]
    public void 识别MultiMc整合包()
    {
        var path = BuildZip("mmc.zip", new Dictionary<string, string>
        {
            ["mmc-pack.json"] = """
            { "components": [ { "uid": "net.minecraft", "version": "1.19.2" },
                              { "uid": "net.fabricmc.fabric-loader", "version": "0.15.11" } ] }
            """,
            ["config/x.txt"] = "x",
        });

        var info = new ModpackInstaller(new GameInstallationService()).Read(path);

        Assert.Equal(ModpackFormat.MultiMc, info.Format);
        Assert.Equal("1.19.2", info.MinecraftVersion);
        Assert.Equal("Fabric", info.Loader);
        Assert.Equal("0.15.11", info.LoaderVersion);
    }

    [Fact]
    public async Task 无法识别版本时安装会明确报错()
    {
        var path = BuildZip("unknown.zip", new Dictionary<string, string> { ["readme.txt"] = "hi" });
        var installer = new ModpackInstaller(new GameInstallationService());

        var result = await installer.InstallAsync(path, Path.Combine(_directory, "game"));

        Assert.False(result.Success);
        Assert.Contains("无法从整合包中识别出 Minecraft 版本", result.Message);
    }

    private string BuildZip(string fileName, Dictionary<string, string> entries)
    {
        var path = Path.Combine(_directory, fileName);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }
}

public class JavaRequirementTests
{
    [Fact]
    public void 从版本JSON读取声明的Java主版本()
    {
        const string json = """
        {
          "id": "26.1",
          "javaVersion": { "component": "java-runtime-epsilon", "majorVersion": 25 }
        }
        """;

        Assert.Equal(25, JavaRuntimeService.ParseDeclaredJavaMajorVersion(json));
    }

    [Fact]
    public void 主版本号写成字符串也能解析()
    {
        Assert.Equal(25, JavaRuntimeService.ParseDeclaredJavaMajorVersion("""{ "javaVersion": { "majorVersion": "25" } }"""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "javaVersion": {} }""")]
    [InlineData("""{ "javaVersion": { "majorVersion": 0 } }""")]
    [InlineData("not json")]
    [InlineData("")]
    public void 缺失或非法的Java声明返回空(string json) =>
        Assert.Null(JavaRuntimeService.ParseDeclaredJavaMajorVersion(json));

    [Fact]
    public void 版本JSON缺失时回退到启发式规则()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsl-java-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 没有版本 JSON（例如目录被删）时不应抛异常，退回旧的启发式判断
            Assert.Equal(
                JavaRuntimeService.RecommendFeatureVersion("26.1"),
                JavaRuntimeService.RequiredFeatureVersion(root, "26.1"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void 沿inheritsFrom向上追溯Java声明()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsl-java-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteVersion(root, "26.1", """{ "id": "26.1", "javaVersion": { "majorVersion": 25 } }""");
            WriteVersion(root, "26.1-fabric-0.16.0", """{ "id": "26.1-fabric-0.16.0", "inheritsFrom": "26.1" }""");

            Assert.Equal(25, JavaRuntimeService.RequiredFeatureVersion(root, "26.1"));
            Assert.Equal(25, JavaRuntimeService.RequiredFeatureVersion(root, "26.1-fabric-0.16.0"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void WriteVersion(string root, string id, string json)
    {
        var directory = Path.Combine(root, "versions", id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }
}
