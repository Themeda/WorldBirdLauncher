using System.Security.Cryptography;
using System.Text;
using McServerList.Core.Launcher;

namespace McServerList.Core.Tests;

public class IntegrityServiceTests
{
    [Fact]
    public async Task 全部哈希匹配时报告健康()
    {
        using var temp = new TempGameRoot();
        temp.WriteVersion("1.20.1", client: "client-jar", libraries: ["libs/a.jar"], assets: ["minecraft/sounds/x.ogg"]);

        var report = await new IntegrityService().CheckAsync(temp.Path, "1.20.1");

        Assert.True(report.IsHealthy, report.Summary);
        Assert.Equal(3, report.Checked);   // 客户端 jar + 1 个库 + 1 个资源
        Assert.Equal(3, report.Passed);
        Assert.Equal(0, report.Missing);
        Assert.Equal(0, report.HashMismatch);
        Assert.Empty(report.Issues);
    }

    [Fact]
    public async Task 能识别缺失文件与哈希不符()
    {
        using var temp = new TempGameRoot();
        temp.WriteVersion("1.20.1", client: "client-jar", libraries: ["libs/a.jar"], assets: ["minecraft/sounds/x.ogg"]);

        // 库文件被删掉、资源文件被改坏
        File.Delete(System.IO.Path.Combine(temp.Path, "libraries", "libs", "a.jar"));
        var assetPath = System.IO.Path.Combine(temp.Path, "assets", "objects");
        var assetFile = Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories).Single();
        await File.WriteAllTextAsync(assetFile, "tampered");

        var report = await new IntegrityService().CheckAsync(temp.Path, "1.20.1");

        Assert.False(report.IsHealthy);
        Assert.Equal(1, report.Missing);
        Assert.Equal(1, report.HashMismatch);
        Assert.Contains(report.Issues, i => i.Reason.Contains("缺失"));
        Assert.Contains(report.Issues, i => i.Reason.Contains("不一致") || i.Reason.Contains("不匹配"));
    }

    [Fact]
    public async Task 版本不存在时返回问题而不是抛异常()
    {
        using var temp = new TempGameRoot();

        var report = await new IntegrityService().CheckAsync(temp.Path, "不存在的版本");

        Assert.False(report.IsHealthy);
        Assert.Equal(0, report.Checked);
        Assert.Single(report.Issues);
    }

    [Fact]
    public async Task 其它系统专用的库会被跳过而不是算缺失()
    {
        using var temp = new TempGameRoot();
        // 造一个"只允许另一个操作系统"的库：在当前系统上必须被跳过
        var otherOs = OperatingSystem.IsWindows() ? "linux" : "windows";
        temp.WriteVersion("1.20.1", client: "client-jar", libraries: ["libs/a.jar"],
            assets: ["minecraft/sounds/x.ogg"], extraForeignLibrary: $"libs/foreign-{otherOs}.jar", foreignOs: otherOs);

        var report = await new IntegrityService().CheckAsync(temp.Path, "1.20.1");

        Assert.True(report.IsHealthy, report.Summary);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(3, report.Checked); // 客户端 + 1 个库 + 1 个资源（不含被跳过的库）
    }

    [Fact]
    public async Task 可选包缺失不算问题()
    {
        using var temp = new TempGameRoot();
        // tv.twitch 是 Mojang 早已移除的 Twitch 直播集成：文件不在也不能报缺失
        temp.WriteVersion("1.7.10", client: "client-jar", libraries: ["libs/a.jar"],
            assets: ["minecraft/sounds/x.ogg"],
            optionalLibrary: ("tv.twitch:twitch-platform:5.16", "tv/twitch/twitch-platform/5.16/twitch-platform-5.16.jar"));

        var report = await new IntegrityService().CheckAsync(temp.Path, "1.7.10");

        Assert.True(report.IsHealthy, report.Summary);
        Assert.Equal(1, report.OptionalSkipped);
        Assert.Equal(0, report.Missing);
        Assert.Contains("可选包缺失 1 个", report.Summary);
    }

    /// <summary>搭一个最小的游戏目录：版本 JSON + 客户端 jar + 库 + 资源索引 + 资源对象。</summary>
    private sealed class TempGameRoot : IDisposable
    {
        public TempGameRoot()
        {
            Path = Directory.CreateTempSubdirectory("mc-integrity-test-").FullName;
        }

        public string Path { get; }

        public void WriteVersion(
            string versionId,
            string client,
            IReadOnlyList<string> libraries,
            IReadOnlyList<string> assets,
            string? extraForeignLibrary = null,
            string? foreignOs = null,
            (string Name, string Path)? optionalLibrary = null)
        {
            var clientBytes = Encoding.UTF8.GetBytes(client);
            var clientSha1 = Sha1(clientBytes);
            var versionDir = System.IO.Path.Combine(Path, "versions", versionId);
            Directory.CreateDirectory(versionDir);
            File.WriteAllBytes(System.IO.Path.Combine(versionDir, versionId + ".jar"), clientBytes);

            var libraryJson = new List<string>();
            foreach (var library in libraries)
            {
                var bytes = Encoding.UTF8.GetBytes("library:" + library);
                var sha1 = Sha1(bytes);
                var full = System.IO.Path.Combine(Path, "libraries", library.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                libraryJson.Add("{\"downloads\":{\"artifact\":{\"path\":\"" + library + "\",\"sha1\":\"" + sha1 + "\"}}}");
            }

            if (extraForeignLibrary is not null && foreignOs is not null)
            {
                // 只声明 rules、文件不写：正常情况应被跳过（不写文件也不该报缺失）
                libraryJson.Add("{\"name\":\"test:foreign:1.0\"," +
                                "\"rules\":[{\"action\":\"allow\",\"os\":{\"name\":\"" + foreignOs + "\"}}]," +
                                "\"downloads\":{\"artifact\":{\"path\":\"" + extraForeignLibrary + "\",\"sha1\":\"0000000000000000000000000000000000000000\"}}}");
            }

            if (optionalLibrary is { } optional)
            {
                // 只声明、不写文件：可选包（如 tv.twitch）缺失不能算缺失
                libraryJson.Add("{\"name\":\"" + optional.Name + "\"," +
                                "\"downloads\":{\"artifact\":{\"path\":\"" + optional.Path + "\",\"sha1\":\"0000000000000000000000000000000000000000\"}}}");
            }

            var indexId = versionId + "-assets";
            var objectEntries = new List<string>();
            foreach (var asset in assets)
            {
                var bytes = Encoding.UTF8.GetBytes("asset:" + asset);
                var sha1 = Sha1(bytes);
                var full = System.IO.Path.Combine(Path, "assets", "objects", sha1[..2], sha1);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                objectEntries.Add("  \"" + asset + "\": {\"hash\":\"" + sha1 + "\",\"size\":" + bytes.Length + "}");
            }

            var indexDir = System.IO.Path.Combine(Path, "assets", "indexes");
            Directory.CreateDirectory(indexDir);
            File.WriteAllText(System.IO.Path.Combine(indexDir, indexId + ".json"),
                "{\"objects\":{\n" + string.Join(",\n", objectEntries) + "\n}}");

            File.WriteAllText(System.IO.Path.Combine(versionDir, versionId + ".json"),
                "{\"id\":\"" + versionId + "\"," +
                "\"downloads\":{\"client\":{\"sha1\":\"" + clientSha1 + "\"}}," +
                "\"assetIndex\":{\"id\":\"" + indexId + "\"}," +
                "\"libraries\":[" + string.Join(",", libraryJson) + "]}");
        }

        private static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 测试清理失败不影响断言
            }
        }
    }
}
