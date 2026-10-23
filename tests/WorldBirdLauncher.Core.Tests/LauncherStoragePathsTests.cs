using WorldBirdLauncher.Core.Storage;

namespace WorldBirdLauncher.Core.Tests;

public sealed class LauncherStoragePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wbl-public-storage-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 应用数据_首次升级复制旧目录且保留原文件()
    {
        var legacy = Path.Combine(_root, "McServerList");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "accounts.dat"), "legacy-account");

        var current = LauncherStoragePaths.ResolveAppDataDirectory(_root);

        Assert.Equal(Path.Combine(_root, "WorldBirdLauncher"), current);
        Assert.Equal("legacy-account", File.ReadAllText(Path.Combine(current, "accounts.dat")));
        Assert.True(File.Exists(Path.Combine(legacy, "accounts.dat")));
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
            // 清理失败不影响测试结论。
        }
    }
}
