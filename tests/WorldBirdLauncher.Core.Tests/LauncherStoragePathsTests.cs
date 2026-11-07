using WorldBirdLauncher.Core.Storage;

namespace WorldBirdLauncher.Core.Tests;

public sealed class LauncherStoragePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wbl-public-storage-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void 应用数据_只使用WorldBirdLauncher目录()
    {
        var legacy = Path.Combine(_root, "McServerList");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "accounts.dat"), "legacy-account");

        var current = LauncherStoragePaths.ResolveAppDataDirectory(_root);

        Assert.Equal(Path.Combine(_root, "WorldBirdLauncher"), current);
        Assert.False(Directory.Exists(current));
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
