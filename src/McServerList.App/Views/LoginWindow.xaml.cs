using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using McServerList.App.Services;
using McServerList.Core.Launcher;

namespace McServerList.App.Views;

/// <summary>按账户类型弹出的登录窗口（微软 / 外置 / 离线 / 统一通行证）。</summary>
public partial class LoginWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private readonly AccountKind _kind;
    private CancellationTokenSource? _microsoftCts;
    private MicrosoftAuth.DeviceCode? _microsoftDevice;

    public LoginWindow(AccountKind kind)
    {
        InitializeComponent();
        _kind = kind;

        // ★ 弹窗高度按屏幕工作区限制（用户反馈：统一通行证要填 4 个框，固定 360 高时密码框被挤出可视区）。
        //   这里用"内容自适应 + 上限 = 工作区 ×0.8（夹在 520~760）"，超出的部分由表单区的 ScrollViewer 滚动，
        //   标题栏与底部按钮固定可见。
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Min(760, Math.Max(520, Math.Round(workArea.Height * 0.8)));
        Width = Math.Max(360, Math.Min(440, workArea.Width - 80));

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        var offline = kind == AccountKind.离线;
        var yggdrasil = kind == AccountKind.外置;
        var unified = kind == AccountKind.统一通行证;

        TitleText.Text = kind switch
        {
            AccountKind.微软 => "微软正版登录",
            AccountKind.外置 => "外置登录（Yggdrasil）",
            AccountKind.离线 => "离线登录",
            _ => "统一通行证登录",
        };

        HintText.Text = kind switch
        {
            AccountKind.微软 => "微软账号授权后即可正版进服。启动器不会接触你的账号密码——授权在微软官方页面完成。",
            AccountKind.外置 => "使用皮肤站账号登录；启动游戏时会自动注入 authlib-injector。",
            AccountKind.离线 => "输入一个名字即可进入游戏，只能进入关闭正版验证的服务器。",
            _ => "统一通行证（Nide8）：账号从服主提供的注册页注册，服务器 ID 也由服主提供。",
        };

        // 外置登录填"皮肤站地址"，统一通行证填"登录站点地址 + 服务器 ID"
        ServerLabel.Visibility = yggdrasil || unified ? Visibility.Visible : Visibility.Collapsed;
        ServerUrlBox.Visibility = yggdrasil || unified ? Visibility.Visible : Visibility.Collapsed;
        ServerIdLabel.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        ServerIdBox.Visibility = unified ? Visibility.Visible : Visibility.Collapsed;
        if (unified)
        {
            ServerLabel.Text = "站点地址（默认 auth.mc-user.com:233）";
            ServerUrlBox.Text = UnifiedPassAuth.DefaultBaseUrl;
        }

        PasswordLabel.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        PasswordBox.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        UserLabel.Text = offline ? "游戏名" : "邮箱 / 用户名";

        if (kind == AccountKind.微软)
        {
            // 微软登录不填账号密码：整块换成"设备代码 + 浏览器授权"面板
            MicrosoftPanel.Visibility = Visibility.Visible;
            ServerLabel.Visibility = Visibility.Collapsed;
            ServerUrlBox.Visibility = Visibility.Collapsed;
            ServerIdLabel.Visibility = Visibility.Collapsed;
            ServerIdBox.Visibility = Visibility.Collapsed;
            UserLabel.Visibility = Visibility.Collapsed;
            UserBox.Visibility = Visibility.Collapsed;
            PasswordLabel.Visibility = Visibility.Collapsed;
            PasswordBox.Visibility = Visibility.Collapsed;
            StatusText.Visibility = Visibility.Collapsed;
            LoginButton.Visibility = Visibility.Collapsed;
            Loaded += async (_, _) => await StartMicrosoftLoginAsync();
            Closed += (_, _) => _microsoftCts?.Cancel();
        }
        else
        {
            MicrosoftPanel.Visibility = Visibility.Collapsed;
            LoginButton.IsEnabled = true;
        }
    }

    public AccountProfile? Result { get; private set; }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        StatusText.Text = "正在登录…";
        try
        {
            switch (_kind)
            {
                case AccountKind.离线:
                    if (!AppEdition.AllowsOfflineLogin)
                    {
                        StatusText.Text = "此版本不提供离线登录，请使用微软正版 / 外置登录 / 统一通行证。";
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(UserBox.Text))
                    {
                        StatusText.Text = "请输入游戏名。";
                        return;
                    }

                    Result = _session.Accounts.LoginOffline(UserBox.Text.Trim());
                    break;

                case AccountKind.外置:
                    if (string.IsNullOrWhiteSpace(ServerUrlBox.Text) ||
                        string.IsNullOrWhiteSpace(UserBox.Text) ||
                        string.IsNullOrEmpty(PasswordBox.Password))
                    {
                        StatusText.Text = "请填写皮肤站地址、账号与密码。";
                        return;
                    }

                    Result = await _session.Accounts.LoginYggdrasilAsync(
                        ServerUrlBox.Text.Trim(), UserBox.Text.Trim(), PasswordBox.Password);
                    break;

                case AccountKind.统一通行证:
                    if (string.IsNullOrWhiteSpace(ServerIdBox.Text) ||
                        string.IsNullOrWhiteSpace(UserBox.Text) ||
                        string.IsNullOrEmpty(PasswordBox.Password))
                    {
                        StatusText.Text = "请填写服务器 ID、账号与密码。";
                        return;
                    }

                    Result = await _session.Accounts.LoginUnifiedPassAsync(
                        ServerIdBox.Text.Trim(),
                        ServerUrlBox.Text.Trim(),
                        UserBox.Text.Trim(),
                        PasswordBox.Password);
                    break;

                default:
                    StatusText.Text = "该登录方式暂未开放。";
                    return;
            }

            _session.AddAccount(Result);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"登录失败：{ex.Message}";
        }
        finally
        {
            LoginButton.IsEnabled = _kind != AccountKind.微软;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _microsoftCts?.Cancel();
        DialogResult = false;
        Close();
    }

    // ---------- 微软正版登录（设备代码流） ----------

    private async Task StartMicrosoftLoginAsync()
    {
        _microsoftCts?.Cancel();
        _microsoftCts?.Dispose();
        _microsoftCts = new CancellationTokenSource();
        var token = _microsoftCts.Token;

        MsCodeBox.Text = string.Empty;
        MsUrlText.Text = string.Empty;
        MsStatusText.Text = "正在向微软申请一次性登录代码…";
        try
        {
            _microsoftDevice = await MicrosoftAuth.StartDeviceCodeAsync(token);
            MsCodeBox.Text = _microsoftDevice.UserCode;
            MsUrlText.Text = _microsoftDevice.VerificationUri;
            try
            {
                Clipboard.SetText(_microsoftDevice.UserCode);
                MsStatusText.Text =
                    $"代码已复制。请在 {Math.Max(1, _microsoftDevice.ExpiresInSeconds / 60)} 分钟内完成授权；窗口会自动检测结果。";
            }
            catch (Exception)
            {
                MsStatusText.Text = "请在浏览器中打开登录页并输入上面的代码；窗口会自动检测结果。";
            }

            var progress = new Progress<string>(text => MsStatusText.Text = text);
            var msa = await MicrosoftAuth.PollForTokenAsync(_microsoftDevice, progress, token);
            MsStatusText.Text = "微软授权成功，正在验证 Xbox / Minecraft…";
            var profile = await MicrosoftAuth.CompleteAsync(msa, token);
            _session.AddAccount(profile);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            // 用户关窗 / 点重新获取
        }
        catch (MicrosoftAuthException ex)
        {
            MsStatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            MsStatusText.Text = "登录失败：" + ex.Message;
        }
    }

    private void OnCopyMicrosoftCode(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MsCodeBox.Text))
        {
            try
            {
                Clipboard.SetText(MsCodeBox.Text);
                MsStatusText.Text = "代码已复制到剪贴板。";
            }
            catch (Exception ex)
            {
                MsStatusText.Text = "复制失败：" + ex.Message;
            }
        }
    }

    private void OnOpenMicrosoftPage(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MsUrlText.Text))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(MsUrlText.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MsStatusText.Text = "打开浏览器失败，请手动访问：" + MsUrlText.Text + "（" + ex.Message + "）";
        }
    }

    private async void OnRetryMicrosoftCode(object sender, RoutedEventArgs e) => await StartMicrosoftLoginAsync();
}
