using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 账号管理：列出本地保存的全部账号（**同一种登录方式可以有多条**），支持切换 / 登出 / 删除。
/// 「登出」只清 token、条目保留（显示为已登出），「删除」才真的从列表里去掉。
/// </summary>
public partial class AccountSwitcherWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private readonly SkinService _skins = new();

    /// <summary>已经点过一次「删除」、正等第二次确认的账号（两步删除，防手滑）。</summary>
    private AccountProfile? _pendingDelete;

    public AccountSwitcherWindow()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Rebuild();
    }

    /// <summary>用户点了「使用」的账号（取消 / 关闭时为 null）。</summary>
    public AccountProfile? SwitchedTo { get; private set; }

    private void Rebuild()
    {
        AccountPanel.Children.Clear();
        _session.RefreshAccounts();

        if (_session.AccountList.Count == 0)
        {
            AccountPanel.Children.Add(new TextBlock
            {
                Text = "还没有保存的账号。点「添加新账号」登录一个。",
                FontSize = 12,
                Margin = new Thickness(2, 10, 2, 6),
                Foreground = (Brush)FindResource("TextMutedBrush"),
            });
            return;
        }

        foreach (var account in _session.AccountList.ToList())
        {
            AccountPanel.Children.Add(BuildRow(account));
        }
    }

    private Border BuildRow(AccountProfile account)
    {
        var isCurrent = IsCurrent(account);
        var signedOut = AccountService.IsSignedOut(account);
        var awaitingDelete = _pendingDelete is { } pending && SameAccount(pending, account);

        var row = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        if (isCurrent)
        {
            // 当前账号高亮（局部值优先于 Style 里的背景）
            row.Background = (Brush)FindResource("AccentSoftBrush");
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 头像：先显示首字母，皮肤取回来之后换成头部图（取不到就保留首字母）
        var letterText = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(account.Name) ? "?" : account.Name[..1].ToUpperInvariant(),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var headImage = new Image
        {
            Stretch = Stretch.UniformToFill,
        };
        RenderOptions.SetBitmapScalingMode(headImage, BitmapScalingMode.NearestNeighbor);
        var avatar = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("AccentSoftBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new Grid
            {
                Children = { letterText, headImage },
            },
        };
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);
        _ = LoadAvatarAsync(account, headImage);

        var info = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = account.Name + (isCurrent ? "　（当前）" : string.Empty),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{DescribeKind(account.Kind)} · {(signedOut ? "已登出（需要重新登录）" : "已登录")}"
                   + (string.IsNullOrWhiteSpace(account.ServerUrl) ? string.Empty : $" · {account.ServerUrl}"),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextMutedBrush"),
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // 按钮统一 72 宽（三个字也放得下，不会再被截断）
        buttons.Children.Add(RowButton(isCurrent ? "使用中" : "使用", !isCurrent, 72, false, () => Use(account)));
        buttons.Children.Add(RowButton("登出", !signedOut, 72, false, () => SignOut(account)));
        buttons.Children.Add(RowButton(awaitingDelete ? "确认删除" : "删除", true, 84, true, () => Delete(account)));
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);

        row.Child = grid;
        return row;
    }

    private Button RowButton(string text, bool enabled, double width, bool danger, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Height = 28,
            MinWidth = width,
            FontSize = 12,
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Style = (Style)FindResource("GhostButton"),
            IsEnabled = enabled,
        };
        if (danger)
        {
            // 删除按钮：红字 + 红边（二次确认时把底色也点成红色，进一步提醒）
            var dangerBrush = (Brush)FindResource("DangerBrush");
            button.Foreground = dangerBrush;
            button.BorderBrush = dangerBrush;
            if (text == "确认删除")
            {
                button.Background = dangerBrush;
                button.Foreground = Brushes.White;
            }
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    private static bool SameAccount(AccountProfile left, AccountProfile right) =>
        left.Kind == right.Kind && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按账户类型取皮肤并裁成头部当头像；取不到就保留首字母。
    /// ★ 注意（AGENTS.md 第 27 条）：皮肤是异步取的，回来时**不一定在 UI 线程**，
    ///   而 `Image.Source` 是 WPF 控件属性，必须切回 UI 线程再赋值，否则抛跨线程异常（会被下面的 catch 吞掉、
    ///   表现就是"头像一直只有首字母"）。
    /// </summary>
    private async Task LoadAvatarAsync(AccountProfile account, Image target)
    {
        try
        {
            var skin = await _skins.ResolveAsync(account).ConfigureAwait(false);
            if (skin is null)
            {
                return;
            }

            // SkinHead 返回的是 Freeze 过的位图，可以安全跨线程传
            var head = SkinHead.FromSkin(skin.SkinPng);
            await Dispatcher.InvokeAsync(() => target.Source = head);
        }
        catch (Exception ex)
        {
            App.LogLine($"账号列表取头像失败（{account.Name}）：{ex.Message}");
        }
    }

    private bool IsCurrent(AccountProfile account) =>
        _session.CurrentAccount is { } current &&
        current.Kind == account.Kind &&
        string.Equals(current.Name, account.Name, StringComparison.OrdinalIgnoreCase);

    private static string DescribeKind(AccountKind kind) => kind switch
    {
        AccountKind.离线 => "离线账户",
        AccountKind.外置 => "外置登录（Yggdrasil）",
        AccountKind.统一通行证 => "统一通行证",
        _ => "微软正版",
    };

    private void Use(AccountProfile account)
    {
        _pendingDelete = null;
        _session.SwitchAccount(account);
        SwitchedTo = account;
        DialogResult = true;
        Close();
    }

    private void SignOut(AccountProfile account)
    {
        _pendingDelete = null;
        if (!MessageWindow.Confirm(
                this,
                "登出账号",
                $"确定要登出「{account.Name}」吗？\n\n只会清掉本地保存的登录凭证，账号条目仍留在列表里，随时可以重新登录。",
                "登出",
                "取消"))
        {
            return;
        }

        _session.SignOutAccount(account);
        Rebuild();
    }

    private void Delete(AccountProfile account)
    {
        // 两步删除：第一次点弹确认框；确认后按钮就地点成「确认删除」，**再点一次**才真删（防手滑）
        if (_pendingDelete is null || !SameAccount(_pendingDelete, account))
        {
            if (!MessageWindow.Confirm(
                    this,
                    "删除账号",
                    $"确定要从本地列表里删除「{account.Name}」吗？\n\n删除后需要重新输入账号密码才能再登录。",
                    "删除",
                    "取消"))
            {
                return;
            }

            _pendingDelete = account;
            Rebuild();
            return;
        }

        _pendingDelete = null;
        _session.RemoveAccount(account);
        Rebuild();
    }

    /// <summary>添加新账号：按当前账户类型弹登录窗（登录成功会自动 Upsert 进列表）。</summary>
    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        if (!AppEdition.AllowsOfflineLogin)
        {
            // 阉割版只有三种登录方式：先让用户选，再弹对应登录窗
            var menu = new ContextMenu();
            foreach (var (label, kind) in new[]
                     {
                         ("微软正版", AccountKind.微软),
                         ("外置登录（Yggdrasil）", AccountKind.外置),
                         ("统一通行证", AccountKind.统一通行证),
                     })
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => OpenLogin(kind);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = sender as UIElement;
            menu.IsOpen = true;
            return;
        }

        OpenLogin(_session.CurrentAccount?.Kind ?? AccountKind.离线);
    }

    private void OpenLogin(AccountKind kind)
    {
        var dialog = new LoginWindow(kind) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Rebuild();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
