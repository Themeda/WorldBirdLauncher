namespace WorldBirdLauncher.Core.Storage;

/// <summary>Canonical WorldBirdLauncher data directory.</summary>
internal static class LauncherStoragePaths
{
    internal const string CurrentAppDataFolderName = "WorldBirdLauncher";

    internal static string ResolveAppDataDirectory(string applicationDataRoot) =>
        Path.Combine(applicationDataRoot, CurrentAppDataFolderName);
}
