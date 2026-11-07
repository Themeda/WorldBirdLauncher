using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Views;

/// <summary>Microsoft account authorization window.</summary>
public partial class LoginWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private CancellationTokenSource? _microsoftCts;
    private MicrosoftAuth.AuthorizationSession? _microsoftSession;

    public LoginWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Min(760, Math.Max(420, Math.Round(workArea.Height * 0.8)));
        Width = Math.Max(360, Math.Min(460, workArea.Width - 80));

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };

        Loaded += async (_, _) => await StartMicrosoftLoginAsync();
        Closed += (_, _) => _microsoftCts?.Cancel();
    }

    public AccountProfile? Result { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _microsoftCts?.Cancel();
        _microsoftSession?.Dispose();
        _microsoftSession = null;
        DialogResult = false;
        Close();
    }

    private async Task StartMicrosoftLoginAsync()
    {
        _microsoftCts?.Cancel();
        _microsoftCts?.Dispose();
        _microsoftSession?.Dispose();
        _microsoftCts = new CancellationTokenSource();
        var token = _microsoftCts.Token;

        MsUrlText.Text = string.Empty;
        MsStatusText.Text = "正在准备浏览器授权…";
        try
        {
            _microsoftSession = MicrosoftAuth.StartAuthorizationCode();
            MsUrlText.Text = _microsoftSession.AuthorizationUri.AbsoluteUri;
            OpenMicrosoftAuthorizationPage();
            MsStatusText.Text = "已打开系统浏览器。请完成微软授权并返回此窗口。";

            var progress = new Progress<string>(text => MsStatusText.Text = text);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var msa = await _microsoftSession.WaitForTokenAsync(progress, timeout.Token);
            MsStatusText.Text = "微软授权成功，正在验证 Xbox / Minecraft…";
            Result = await MicrosoftAuth.CompleteAsync(msa, token);
            _session.AddAccount(Result);
            _microsoftSession.Dispose();
            _microsoftSession = null;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            MsStatusText.Text = "微软授权等待超时，请关闭其他网页后重新开始。";
        }
        catch (OperationCanceledException)
        {
            // The user closed the window or restarted authorization.
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

    private void OnOpenMicrosoftPage(object sender, RoutedEventArgs e)
    {
        if (_microsoftSession is null)
        {
            return;
        }

        try
        {
            OpenMicrosoftAuthorizationPage();
            MsStatusText.Text = "已重新打开系统浏览器。";
        }
        catch (Exception ex)
        {
            MsStatusText.Text =
                "打开浏览器失败，请手动访问：" + MsUrlText.Text + "（" + ex.Message + "）";
        }
    }

    private void OpenMicrosoftAuthorizationPage()
    {
        var session = _microsoftSession
                      ?? throw new InvalidOperationException("微软授权会话尚未准备完成。");
        Process.Start(new ProcessStartInfo(session.AuthorizationUri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }

    private async void OnRetryMicrosoftCode(object sender, RoutedEventArgs e) =>
        await StartMicrosoftLoginAsync();

    protected override void OnClosed(EventArgs e)
    {
        _microsoftCts?.Cancel();
        _microsoftCts?.Dispose();
        _microsoftSession?.Dispose();
        _microsoftSession = null;
        base.OnClosed(e);
    }
}
