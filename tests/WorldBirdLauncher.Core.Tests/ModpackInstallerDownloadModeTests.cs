using System.IO.Compression;
using System.Text;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// Modpack installation boundaries when version downloading is disabled:
/// only archives carrying their own game files are accepted.
/// </summary>
public class ModpackInstallerDownloadModeTests
{
    [Fact]
    public async Task 禁止版本下载时拒绝只有清单的_CurseForge_整合包()
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
                archive, Path.Combine(Path.GetTempPath(), "wbl-lite-test-" + Guid.NewGuid().ToString("N")),
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
    public async Task 禁止版本下载时拒绝没有客户端的完整包()
    {
        var archive = CreateZip(new Dictionary<string, string>
        {
            [".minecraft/versions/1.20.1/1.20.1.json"] = """{"id":"1.20.1","type":"release"}""",
            [".minecraft/mods/a.jar"] = "not-really-a-jar",
        });

        try
        {
            var result = await new ModpackInstaller(new GameInstallationService()).InstallAsync(
                archive, Path.Combine(Path.GetTempPath(), "wbl-lite-test-" + Guid.NewGuid().ToString("N")),
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
        var path = Path.Combine(Path.GetTempPath(), "wbl-lite-pack-" + Guid.NewGuid().ToString("N") + ".zip");
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
