namespace WorldBirdLauncher.Core.Storage;

/// <summary>
/// 品牌迁移兼容层：新目录首次创建时复制旧目录，旧目录保留不删除。
/// </summary>
internal static class LauncherStoragePaths
{
    internal const string CurrentAppDataFolderName = "WorldBirdLauncher";
    internal const string LegacyAppDataFolderName = "McServerList";

    internal static string ResolveAppDataDirectory(string applicationDataRoot)
    {
        var current = Path.Combine(applicationDataRoot, CurrentAppDataFolderName);
        var legacy = Path.Combine(applicationDataRoot, LegacyAppDataFolderName);
        if (Directory.Exists(current) || !Directory.Exists(legacy))
        {
            return current;
        }

        var staging = current + ".migrating-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(legacy, staging);
            Directory.Move(staging, current);
            return current;
        }
        catch
        {
            TryDeleteDirectory(staging);
            return legacy;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 迁移清理失败时回退到旧目录。
        }
    }
}
