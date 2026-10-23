using System.IO;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 游戏异常退出（闪退）时收集崩溃日志。
/// 按用户要求**不截取内容**：把 crash-reports 里的报告与 logs/latest.log **整份**复制出来，
/// 统一放到 %APPDATA%\WorldBirdLauncher\crash-reports\&lt;时间&gt;_&lt;版本&gt;\，供用户直接查看。
/// </summary>
public sealed class CrashReportService
{
    public static string Root => Path.Combine(AppSettings.Directory, "crash-reports");

    /// <summary>
    /// 收集日志；返回保存目录与已复制文件清单。没有可收集的文件时返回 null。
    /// </summary>
    public (string Directory, IReadOnlyList<string> Files)? Collect(
        string gameRoot,
        string versionId,
        string? versionDirectory = null)
    {
        // 崩溃报告整份收（历史报告也一起，便于对比）；日志只收 latest.log——
        // logs 目录里还有几十个滚动的 .log.gz 历史文件，全收会把目录塞满、也让提示列表变成噪音。
        var sources = new List<(DirectoryInfo Directory, bool OnlyLatestLog)>();
        foreach (var root in new[] { versionDirectory, gameRoot })
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var relative in new[] { "crash-reports", "logs" })
            {
                var directory = new DirectoryInfo(Path.Combine(root!, relative));
                if (directory.Exists)
                {
                    sources.Add((directory, relative == "logs"));
                }
            }
        }

        if (sources.Count == 0)
        {
            return null;
        }

        var target = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Sanitize(versionId)}");
        Directory.CreateDirectory(target);

        var copied = new List<string>();
        foreach (var (source, onlyLatestLog) in sources)
        {
            var candidates = onlyLatestLog
                ? source.EnumerateFiles("latest.log", SearchOption.TopDirectoryOnly)
                : source.EnumerateFiles("*", SearchOption.AllDirectories);
            foreach (var file in candidates)
            {
                try
                {
                    // 只收集崩溃报告与日志正文，保留原文件名（同名时加父目录前缀）
                    var name = file.Name;
                    var destination = Path.Combine(target, name);
                    if (File.Exists(destination))
                    {
                        destination = Path.Combine(target, $"{source.Name}_{name}");
                    }

                    File.Copy(file.FullName, destination, overwrite: true);
                    copied.Add(destination);
                }
                catch (Exception ex)
                {
                    App.LogLine($"复制崩溃日志失败（{file.FullName}）：{ex.Message}");
                }
            }
        }

        return copied.Count == 0 ? null : (target, copied);
    }

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
