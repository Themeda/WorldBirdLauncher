using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 「提交服务器」弹窗：先选提交方式（已有宣传贴 / 手动填写），再填对应内容，提交到服务端候选审核队列。
/// 专属客户端只需填**网盘链接**（可多条），客户端不上传文件。
/// </summary>
public partial class SubmitServerWindow : Window
{
    private readonly AppSession _session = AppSession.Current;

    public SubmitServerWindow(Window? owner)
    {
        InitializeComponent();

        // 窗口高度按屏幕工作区自适应（内容多也不会超出屏幕），内容区自己滚动
        var workArea = SystemParameters.WorkArea;
        Height = NetCompat.Clamp(workArea.Height * 0.8, 520, 760);

        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        ModeThread.Checked += (_, _) => SyncMode();
        ModeForm.Checked += (_, _) => SyncMode();
        AddDownloadRow();
        SyncMode();
    }

    private void SyncMode()
    {
        var thread = ModeThread.IsChecked == true;
        ThreadPanel.Visibility = thread ? Visibility.Visible : Visibility.Collapsed;
        FormPanel.Visibility = thread ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAddDownloadRow(object sender, RoutedEventArgs e) => AddDownloadRow();

    private void AddDownloadRow()
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        for (var i = 0; i < 5; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i switch { 0 => new GridLength(120), 1 => new GridLength(1, GridUnitType.Star), 2 => new GridLength(80), 3 => new GridLength(1, GridUnitType.Star), _ => GridLength.Auto } });
        }

        var label = new TextBox { Height = 30, Margin = new Thickness(0, 0, 6, 0), ToolTip = "名称（百度网盘/蓝奏云…）" };
        var url = new TextBox { Height = 30, Margin = new Thickness(0, 0, 6, 0), ToolTip = "下载链接" };
        var code = new TextBox { Height = 30, Margin = new Thickness(0, 0, 6, 0), ToolTip = "提取码" };
        var note = new TextBox { Height = 30, Margin = new Thickness(0, 0, 6, 0), ToolTip = "说明（含 mods/config…）" };
        var remove = new Button { Content = "删除", Style = (Style)FindResource("GhostButton"), Padding = new Thickness(10, 2, 10, 2), FontSize = 11 };
        remove.Click += (_, _) => DownloadRows.Children.Remove(row);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(url, 1);
        Grid.SetColumn(code, 2);
        Grid.SetColumn(note, 3);
        Grid.SetColumn(remove, 4);
        row.Children.Add(label);
        row.Children.Add(url);
        row.Children.Add(code);
        row.Children.Add(note);
        row.Children.Add(remove);
        DownloadRows.Children.Add(row);
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        SubmitButton.IsEnabled = false;
        try
        {
            if (ModeThread.IsChecked == true)
            {
                var url = ThreadUrlBox.Text.Trim();
                if (url.Length == 0)
                {
                    StatusText.Text = "请填写宣传贴网址。";
                    return;
                }

                StatusText.Text = "正在提交…";
                var (ok, message) = await _session.RemoteCatalog.SubmitServerAsync("thread", url, null);
                if (ok)
                {
                    MessageWindow.Info(this, "提交成功", message + "\n\n审核通过后会出现在服务器列表里。");
                    Close();
                }
                else
                {
                    StatusText.Text = message;
                }

                return;
            }

            var name = NameBox.Text.Trim();
            var address = AddressBox.Text.Trim();
            var downloads = ReadDownloads();
            if (name.Length == 0)
            {
                StatusText.Text = "请填写服务器名称。";
                return;
            }

            if (address.Length == 0 && downloads.Count == 0)
            {
                StatusText.Text = "请至少填写服务器地址，或一条专属客户端下载链接。";
                return;
            }

            var entry = new ServerEntry
            {
                Name = name,
                Address = address,
                Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 25565,
                Version = VersionBox.Text.Trim(),
                VersionRange = RangeBox.Text.Trim().Length == 0 ? null : RangeBox.Text.Trim(),
                Loader = (LoaderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "原版",
                Region = "国内",
                // net48 的 StringSplitOptions 没有 TrimEntries，用兼容层的 SplitTrim（切完逐个 Trim）
                Categories = NetCompat.SplitTrim(CategoryBox.Text, ',').Where(v => v.Length > 0).ToArray(),
                Description = DescBox.Text.Trim(),
                LongDescription = LongBox.Text,
                LoginMode = (LoginBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                ServerType = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                QqGroup = QqBox.Text.Trim().Length == 0 ? null : QqBox.Text.Trim(),
                OfficialSite = SiteBox.Text.Trim().Length == 0 ? null : SiteBox.Text.Trim(),
                RequiresCustomClient = CustomClientBox.IsChecked == true,
                ClientDownloads = downloads,
                SourceName = "客户端提交",
            };

            StatusText.Text = "正在提交…";
            var result = await _session.RemoteCatalog.SubmitServerAsync("form", null, entry);
            if (result.Ok)
            {
                MessageWindow.Info(this, "提交成功", result.Message + "\n\n审核通过后会出现在服务器列表里。");
                Close();
            }
            else
            {
                StatusText.Text = result.Message;
            }
        }
        finally
        {
            SubmitButton.IsEnabled = true;
        }
    }

    private IReadOnlyList<ClientDownload> ReadDownloads()
    {
        var result = new List<ClientDownload>();
        foreach (var child in DownloadRows.Children.OfType<Grid>())
        {
            var boxes = child.Children.OfType<TextBox>().ToList();
            if (boxes.Count < 4)
            {
                continue;
            }

            var url = boxes[1].Text.Trim();
            if (url.Length == 0)
            {
                continue;
            }

            result.Add(new ClientDownload
            {
                Label = boxes[0].Text.Trim(),
                Url = url,
                ExtractCode = boxes[2].Text.Trim().Length == 0 ? null : boxes[2].Text.Trim(),
                Note = boxes[3].Text.Trim().Length == 0 ? null : boxes[3].Text.Trim(),
            });
        }

        return result;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
