using System.Windows;
using System.Windows.Input;

namespace McServerList.App.Views;

/// <summary>
/// 与应用同风格的自定义弹窗（圆角无边框 + 主题按钮），替代系统 MessageBox，
/// 保证整个程序的窗口风格一致。
/// </summary>
public partial class MessageWindow : Window
{
    private MessageWindow()
    {
        InitializeComponent();
    }

    /// <summary>只有一个「确定」按钮的提示。</summary>
    public static void Info(Window? owner, string title, string message, string okText = "确定")
    {
        var window = Create(owner, title, message, okText, cancelText: null);
        window.ShowDialog();
    }

    /// <summary>「确定 / 取消」确认框，返回用户是否点了确定。</summary>
    public static bool Confirm(Window? owner, string title, string message,
        string okText = "确定", string cancelText = "取消")
    {
        var window = Create(owner, title, message, okText, cancelText);
        return window.ShowDialog() == true;
    }

    private static MessageWindow Create(Window? owner, string title, string message, string okText, string? cancelText)
    {
        var window = new MessageWindow
        {
            Title = title,
        };
        window.TitleText.Text = title;
        window.MessageText.Text = message;
        window.OkButton.Content = okText;

        if (owner is not null && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        if (cancelText is null)
        {
            window.CancelButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            window.CancelButton.Content = cancelText;
        }

        return window;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
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
