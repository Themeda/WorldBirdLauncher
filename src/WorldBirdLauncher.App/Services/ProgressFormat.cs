using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 安装 / 下载进度的文案格式化：文件大小、下载速度、剩余时间。
///
/// 背景（用户反馈）：大文件（&gt;4MB）走限速源时百分比长时间几乎不动，看着像卡死；
/// 所以进度文案里要能直接看到「当前文件 3.4 / 25.0 MB · 0.9 MB/s · 剩余约 26 秒」。
/// </summary>
internal static class ProgressFormat
{
    public static string Size(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return bytes < 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024):0.#} MB"
            : $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    /// <summary>速度：&lt;1MB/s 用 KB/s（这个区间的用户最容易怀疑"是不是卡住了"）。</summary>
    public static string Speed(double bytesPerSecond) => bytesPerSecond < 1024 * 1024
        ? $"{bytesPerSecond / 1024:0} KB/s"
        : $"{bytesPerSecond / (1024 * 1024):0.0} MB/s";

    public static string Duration(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes} 分 {remaining.Seconds} 秒";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒";
    }

    /// <summary>
    /// 「已下/总量 · 速度 · 剩余」这一串数字（没有可显示的返回 null）。
    /// 文件名由调用方各自拼（下载任务列表里文件名是单独一行的）。
    /// </summary>
    public static string? Metrics(InstallProgress progress)
    {
        var parts = new List<string>();
        if (progress.File is not null)
        {
            parts.Add(progress.FileTotal > 0
                ? $"{Size(progress.FileDone)} / {Size(progress.FileTotal)}"
                : Size(progress.FileDone));
        }

        if (progress.BytesPerSecond > 0)
        {
            parts.Add(Speed(progress.BytesPerSecond));
        }

        if (progress.Remaining is { TotalSeconds: > 0 and < 86400 } remaining)
        {
            parts.Add($"剩余约 {Duration(remaining)}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// 一行式描述：有当前文件时给「大小 · 速度 · 剩余」，否则退回 <see cref="InstallProgress.Detail"/>。
    /// </summary>
    public static string? Describe(InstallProgress progress)
    {
        var metrics = Metrics(progress);
        return progress.File is null ? progress.Detail ?? metrics : metrics ?? progress.Detail;
    }
}
