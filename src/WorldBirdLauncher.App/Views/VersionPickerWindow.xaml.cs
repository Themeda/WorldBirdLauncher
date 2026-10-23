using System.Windows;
using System.Windows.Input;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 跨版本服务器（声明了版本区间）在直连启动时用的"让用户挑版本"弹窗：
/// 列出本机已装且落在服务器支持区间内的版本。
/// </summary>
public partial class VersionPickerWindow : Window
{
    public VersionPickerWindow(Window? owner, string serverName, string supported, IReadOnlyList<string> candidates)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        MessageText.Text =
            $"服务器「{serverName}」支持 {supported}，当前选的版本不匹配。\n" +
            "本机已装且匹配的版本如下，请挑一个要用它启动的：";
        VersionBox.ItemsSource = candidates;
        VersionBox.SelectedIndex = candidates.Count > 0 ? 0 : -1;
    }

    /// <summary>用户选择的版本目录名；取消时为 null。</summary>
    public string? SelectedVersionId { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (VersionBox.SelectedItem is not string selected || selected.Length == 0)
        {
            return;
        }

        SelectedVersionId = selected;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
