﻿﻿using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using McServerList.App.Services;
using McServerList.Core.Launcher;

namespace McServerList.App.Views;

/// <summary>
/// 皮肤展示：用 WebView2 嵌 skinview3d 渲染可旋转的 3D 皮肤；
/// 皮肤来源按账户类型解析（正版→Mojang、外置→皮肤站、离线→按 ID 查 Mojang，查不到用默认）。
/// </summary>
public partial class SkinPage : UserControl
{
    private readonly AppSession _session = AppSession.Current;
    private readonly SkinService _skins = new();
    private bool _initialized;
    private bool _busy;
    private bool _reloadQueued;
    private bool _suppressBackgroundPick;
    private bool _webViewRuntimeProbed;
    private bool _webViewRuntimeOk;

    /// <summary>WebView2 运行时（Microsoft Edge WebView2 Runtime）缺失时提示里的官方下载地址。</summary>
    private const string WebView2RuntimeUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    /// <summary>skinview3d 打包脚本（内嵌资源，避免依赖被墙的 CDN）。</summary>
    private static readonly Lazy<string> SkinViewScript = new(() =>
    {
        using var stream = typeof(SkinPage).Assembly
            .GetManifestResourceStream("McServerList.App.Assets.skinview3d.bundle.js");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public SkinPage()
    {
        InitializeComponent();
        // 账户在启动页被切换时，皮肤来源与 3D 视图自动跟随
        _session.AccountChanged += (_, _) => Dispatcher.InvokeAsync(() => _ = ReloadAsync());
        Loaded += async (_, _) =>
        {
            SyncBackgroundOptions();
            await ReloadAsync();
        };
    }

    private string CurrentBackground => _session.Settings.SkinBackground == "sky" ? "sky" : "dark";

    private void SyncBackgroundOptions()
    {
        _suppressBackgroundPick = true;
        DarkBackgroundOption.IsChecked = CurrentBackground == "dark";
        SkyBackgroundOption.IsChecked = CurrentBackground == "sky";
        _suppressBackgroundPick = false;
    }

    /// <summary>
    /// 离线账户兜底：挑一张本机的皮肤 PNG（按「账户类型 + 账户名」记住，只存路径不拷贝文件）。
    /// </summary>
    private void OnPickLocalSkin(object sender, RoutedEventArgs e)
    {
        var account = _session.CurrentAccount;
        if (account is null)
        {
            HintText.Text = "还没有账户：请先到启动页选择账户类型登录。";
            HintPanel.Visibility = Visibility.Visible;
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择皮肤文件（Minecraft 皮肤 PNG）",
            Filter = "Minecraft 皮肤 (*.png)|*.png|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        LocalSkinStore.Set(account, dialog.FileName);
        _ = ReloadAsync();
    }

    /// <summary>清除本账户指定的本地皮肤，回到按账户类型自动解析。</summary>
    private void OnClearLocalSkin(object sender, RoutedEventArgs e)
    {
        if (_session.CurrentAccount is { } account)
        {
            LocalSkinStore.Set(account, null);
        }

        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_busy)
        {
            _reloadQueued = true; // 正在加载时又收到刷新请求，等这次结束再跑一遍
            return;
        }

        _busy = true;
        var account = _session.CurrentAccount;
        AccountText.Text = account is null
            ? "还没有账户（请到启动页选择账户类型登录）"
            : $"{account.Name} · {account.Kind}";

        HintText.Text = "正在准备 3D 视图…";
        HintPanel.Visibility = Visibility.Visible;
        try
        {
            if (!_initialized)
            {
                // ★ 先探一下本机有没有 WebView2 运行时：Win10 1809 干净系统可能没装，
                //   直接 EnsureCoreWebView2Async 会抛异常（以前只显示一句"可能缺少运行时"，没有下载入口）。
                if (!WebViewRuntimeAvailable())
                {
                    ShowRuntimeMissingHint(null);
                    return;
                }

                await SkinView.EnsureCoreWebView2Async();
                // 空 HWND 的默认底色是白的 —— 设成当前主题底色，连"白屏一瞬"都没有
                SkinView.DefaultBackgroundColor = BackgroundColor();
                // 页面还没画出来之前先藏着：否则 WebView2 初始化那 1 秒会露出一块纯色空白，
                // 而这期间 WPF 那层的转圈被它盖住（airspace）→ 看起来像"提示突然没了"。
                SkinView.Visibility = Visibility.Collapsed;
                // 页面里 skinview3d 画出模型第一帧后会 postMessage('skin-ready')，这里记一行耗时
                SkinView.CoreWebView2.WebMessageReceived += OnSkinWebMessage;
                _initialized = true;
            }

            // ★ 2026-10-08 修的"先白屏、loading 只闪 0.1 秒"：
            //   以前要等皮肤解析完（联网）**才**导航，这期间 WebView2 是个**空的白色 HWND**；
            //   等导航发生时模型几乎同时就绪，页面里的转圈就只剩 0.1 秒。
            //   现在**先把 loading 页挂上去**（第一眼就是转圈），皮肤解析完再 postMessage 推给页面。
            _pageShownAt = DateTime.UtcNow;
            _pageReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            SkinView.NavigateToString(SkinPageHtml.Build(CurrentBackground, SkinViewScript.Value));

            // 解析皮肤（网络请求，可能几百毫秒~几秒；这段时间用户在看着转圈，不是白屏）
            var skin = await _skins.ResolveAsync(account);
            var skinDataUri = skin is null
                ? DefaultSkinDataUri()
                : "data:image/png;base64," + Convert.ToBase64String(skin.SkinPng);
            var isSlim = skin?.IsSlim == true;

            SourceText.Text = account is null
                ? "皮肤来源：未选择账户"
                : skin is null
                    ? "皮肤来源：默认皮肤（该 ID 未在正版库中找到，或离线 / 未取到皮肤）"
                    : $"皮肤来源：{DescribeSource(skin.Source)}" + (isSlim ? "（纤细模型 Alex）" : "（经典模型 Steve）");

            // 指定过本地皮肤文件才显示「清除」入口
            ClearLocalSkinButton.Visibility = LocalSkinStore.Get(account) is null
                ? Visibility.Collapsed
                : Visibility.Visible;

            // 等页面把监听器装好（页面会先 postMessage('page-ready')），免得消息发早了丢掉
            await Task.WhenAny(_pageReady.Task, Task.Delay(4000));
            // 页面已经画出自己的转圈了，这时才把 WebView2 露出来（没等到也露出来，免得一直藏着）
            SkinView.Visibility = Visibility.Visible;
            PostToPage(new { type = "skin", url = skinDataUri, slim = isSlim });
            HintPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            App.LogLine($"皮肤展示失败：{ex}");
            if (ex is WebView2RuntimeNotFoundException
                || ex.Message.IndexOf("WebView2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ShowRuntimeMissingHint(ex);
            }
            else
            {
                HintPanel.Visibility = Visibility.Visible;
                HintText.Text = $"无法加载 3D 皮肤视图：{ex.Message}\n（当前网络不可用或皮肤图片拉取失败；3D 视图需要 WebView2 运行时）";
            }
        }
        finally
        {
            _busy = false;
            if (_reloadQueued)
            {
                _reloadQueued = false;
                _ = ReloadAsync();
            }
        }
    }

    /// <summary>页面里的 skinview3d 报"首帧已渲染"用的时间戳（用来算 3D 模型加载耗时）。</summary>
    private DateTime _pageShownAt;

    /// <summary>页面把监听器装好并 postMessage('page-ready') 后完成（只在本次导航内有效）。</summary>
    private TaskCompletionSource<bool>? _pageReady;

    private void OnSkinWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var message = e.TryGetWebMessageAsString();
            if (message == "page-ready")
            {
                _pageReady?.TrySetResult(true);
                // 页面（含它自带的转圈）已经画出来了，可以放心把 WebView2 露出来
                SkinView.Visibility = Visibility.Visible;
                return;
            }

            if (message != "skin-ready")
            {
                return;
            }

            var elapsed = (DateTime.UtcNow - _pageShownAt).TotalMilliseconds;
            App.LogLine($"3D 皮肤首帧已渲染（{elapsed:0} ms）");
        }
        catch (Exception ex)
        {
            App.LogLine($"皮肤页消息处理失败：{ex.Message}");
        }
    }

    /// <summary>往页面里推一条 JSON 消息（页面用 window.chrome.webview 的 message 事件接）。</summary>
    private void PostToPage(object payload)
    {
        try
        {
            SkinView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            App.LogLine($"皮肤页推送消息失败：{ex.Message}");
        }
    }

    /// <summary>WebView2 空 HWND 的默认底色（与页面里的底色一致，避免出现白底）。</summary>
    private System.Drawing.Color BackgroundColor() => CurrentBackground == "sky"
        ? System.Drawing.Color.FromArgb(0xD8, 0xEC, 0xFF)
        : System.Drawing.Color.FromArgb(0x1B, 0x1C, 0x1F);

    /// <summary>本机是否有可用的 WebView2 运行时（只探一次，结果缓存）。</summary>
    private bool WebViewRuntimeAvailable()
    {
        if (_webViewRuntimeProbed)
        {
            return _webViewRuntimeOk;
        }

        _webViewRuntimeProbed = true;
        try
        {
            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            _webViewRuntimeOk = !string.IsNullOrWhiteSpace(version);
            if (_webViewRuntimeOk)
            {
                App.LogLine($"WebView2 运行时版本：{version}");
            }
        }
        catch (Exception ex)
        {
            _webViewRuntimeOk = false;
            App.LogLine($"WebView2 运行时检测失败（按未安装处理）：{ex.Message}");
        }

        return _webViewRuntimeOk;
    }

    /// <summary>
    /// 缺 WebView2 运行时时的提示：说清影响 + 给一个**可点击**的官方下载链接，并说明其它功能不受影响。
    /// （只写一句"可能缺少运行时"用户不知道去哪儿装。）
    /// </summary>
    private void ShowRuntimeMissingHint(Exception? error)
    {
        HintText.Inlines.Clear();
        HintText.Inlines.Add(new Run("本机没有安装「Microsoft Edge WebView2 运行时」，3D 皮肤预览用不了。\n"));
        if (error is not null)
        {
            HintText.Inlines.Add(new Run($"（{error.Message}）\n"));
        }

        var link = new Hyperlink(new Run("点这里下载 WebView2 运行时"))
        {
            ToolTip = WebView2RuntimeUrl,
        };
        link.Click += (_, _) => OpenRuntimeDownloadPage();
        HintText.Inlines.Add(link);
        HintText.Inlines.Add(new Run("\n装完重启启动器即可；服务器列表、启动游戏、资源下载都不受影响。"));
        HintPanel.Visibility = Visibility.Visible;
        SkinView.Visibility = Visibility.Collapsed;
    }

    private void OpenRuntimeDownloadPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WebView2RuntimeUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.LogLine($"打开 WebView2 下载页失败：{ex.Message}");
        }
    }

    private static string DescribeSource(string source) =>
        source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new Uri(source).Host
            : source;

    private void OnReload(object sender, RoutedEventArgs e) => _ = ReloadAsync();

    private void OnBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressBackgroundPick)
        {
            return;
        }

        _session.Settings.SkinBackground = SkyBackgroundOption.IsChecked == true ? "sky" : "dark";
        _session.Settings.Save();

        // 页内直接切背景（不重新导航）——重载模型会再走一遍 loading，切换体验很碎
        if (_initialized && SkinView.CoreWebView2 is not null)
        {
            SkinView.DefaultBackgroundColor = BackgroundColor();
            PostToPage(new { type = "background", background = CurrentBackground });
            return;
        }

        _ = ReloadAsync();
    }

    /// <summary>内置默认皮肤（Steve）：皮肤站/官方都取不到时兜底。</summary>
    private static string DefaultSkinDataUri() =>
        "https://textures.minecraft.net/texture/31f477eb1a7beee631c2ca64d06f8f68fa93a3386d04452ab27f43acdf1b60cb";
}
