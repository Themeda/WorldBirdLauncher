using System.Configuration;
using System.Data;
using System.Windows;

namespace McServerList.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 主题要在第一个窗口建出来之前应用（Theme.xaml 里全是 DynamicResource，
        // 这里换掉同名画刷，界面一开就是用户选的主题，不会闪一下默认色）
        Services.ThemeManager.Apply(Services.AppSession.Current.Settings.ThemeMode);

        // 兜底：把未处理异常写进 %APPDATA%\McServerList\launcher-error.log，
        // 免得再出现"程序无声消失、只能靠事件查看器猜"的情况。
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            // UI 线程的偶发异常不至于让整个启动器（以及托盘的"游戏结束后回来"）失效
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
    }

    private static void Log(Exception? exception)
    {
        LogLine(exception?.ToString() ?? "(未知异常)");
    }

    /// <summary>把诊断信息追加到 %APPDATA%\McServerList\launcher-error.log（也用于托盘流程排查）。</summary>
    internal static void LogLine(string message)
    {
        try
        {
            Directory.CreateDirectory(Services.AppSettings.Directory);
            var logPath = Path.Combine(Services.AppSettings.Directory, "launcher-error.log");
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > 1_000_000)
            {
                File.Delete(logPath); // 简单限长，免得常年累月越写越大
            }

            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 记日志本身失败就算了
        }
    }
}
