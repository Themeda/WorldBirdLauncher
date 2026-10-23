using System.IO.Compression;
using System.Text;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 阉割版（allowVersionDownload=false）的整合包边界（2026-10-20 用户确认）：
/// 不替用户下载游戏版本；只有自带游戏本体的离线完整包才允许解析。
/// </summary>
public class ModpackInstallerLiteTests
{
    [Fact]
    public async Task 阉割版拒绝只有清单的_CurseForge_整合包()
    {
        var archive = CreateZip(new Dictionary<string, string>
        {
            ["manifest.json"] = """
                {"minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0","primary":true}]},
                 "name":"清单包","version":"1.0","author":"tester","files":[]}
                """,
            ["overrides/config/a.txt"] = "x",
        });

        try
        {
            var result = await new ModpackInstaller(new GameInstallationService()).InstallAsync(
                archive, Path.Combine(Path.GetTempPath(), "mcsl-lite-test-" + Guid.NewGuid().ToString("N")),
                allowVersionDownload: false);

            Assert.False(result.Success);
            Assert.Contains("不包含游戏本体", result.Message);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [Fact]
    public async Task 阉割版拒绝没有_jar_的离线包()
    {
        var archive = CreateZip(new Dictionary<string, string>
        {
            [".minecraft/versions/1.20.1/1.20.1.json"] = """{"id":"1.20.1","type":"release"}""",
            [".minecraft/mods/a.jar"] = "not-really-a-jar",
        });

        try
        {
            var result = await new ModpackInstaller(new GameInstallationService()).InstallAsync(
                archive, Path.Combine(Path.GetTempPath(), "mcsl-lite-test-" + Guid.NewGuid().ToString("N")),
                allowVersionDownload: false);

            Assert.False(result.Success);
            Assert.Contains("不包含游戏本体", result.Message);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    private static string CreateZip(IReadOnlyDictionary<string, string> files)
    {
        var path = Path.Combine(Path.GetTempPath(), "mcsl-lite-pack-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in files)
        {
            var entry = archive.CreateEntry(pair.Key);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(pair.Value);
            stream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }
}
