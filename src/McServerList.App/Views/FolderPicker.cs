using System.Windows;

namespace McServerList.App.Views;

/// <summary>
/// 选文件夹对话框。
///
/// `Microsoft.Win32.OpenFolderDialog` 是 **.NET 8 的 WPF 才有的**（也是我们当初选它的原因：原生、不需要 WinForms）。
/// net48 上没有这个类型，退回 WinForms 的 `FolderBrowserDialog`（本项目本来就开了 UseWindowsForms 给托盘图标用）。
/// </summary>
internal static class FolderPicker
{
    /// <summary>弹一个选目录的对话框；用户取消返回 <c>null</c>。</summary>
    public static string? PickFolder(Window? owner, string title, string? initialDirectory = null)
    {
#if NET8_0_OR_GREATER
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        if (owner is not null)
        {
            return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
#else
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = title,
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.SelectedPath = initialDirectory;
        }

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
#endif
    }
}
