using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using McServerList.App.Services;
using McServerList.Core.Launcher;

namespace McServerList.App.Views;

/// <summary>
/// 「安装版本」弹窗：
/// 第一行选游戏版本（可输入），第二行选加载器与版本（只列适配该游戏版本的），
/// 第三行选光影与版本（Iris / OptiFine），另可自定义版本目录名。
/// 已存在的版本名会提示用户改名或确认覆盖重装。
/// </summary>
public partial class InstallVersionWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private readonly ModDownloadService _mods = new();
    private List<GameVersionInfo> _releases = [];
    private HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    /// <summary>该加载器在当前 Java 下"一个合适的版本都没有"时的原因（安装时兜底拦截用）。</summary>
    private string? _unsuitableLoaderReason;

    public InstallVersionWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadReleasesAsync();

        // 可编辑 ComboBox：用户敲字时也要刷新"加载器/光影可选版本"
        GameVersionBox.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => { if (!_loading) RefreshDependentLists(); }));
    }

    /// <summary>安装成功后的最终版本目录名（供设置页刷新）。</summary>
    public string? InstalledVersionId { get; private set; }

    private string GameVersion => GameVersionBox.Text.Trim();

    private string SelectedLoader =>
        (LoaderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "不安装";

    private string SelectedShader =>
        (ShaderBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "不安装";

    private async Task LoadReleasesAsync()
    {
        StatusText.Text = "正在获取远程版本列表…";
        try
        {
            var remote = await _session.Installation.GetRemoteVersionsAsync();
            _releases = remote.Where(v => v.Type == "release").ToList();
            _installed = _session.Installation.GetInstalledVersions(_session.GameRoot)
                .Where(v => v.IsUsable)
                .Select(v => v.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _loading = true;
            GameVersionBox.ItemsSource = _releases.Select(v => v.Id).ToList();
            GameVersionBox.Text = _session.CurrentVersionId is { Length: > 0 } current && _releases.Any(v => v.Id == current)
                ? current
                : _releases.FirstOrDefault()?.Id ?? string.Empty;
            VersionNameBox.Text = GameVersion;
            _loading = false;

            UpdateInstalledHint();
            StatusText.Text = $"共 {_releases.Count} 个正式版可选（也可直接输入版本号）。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取版本列表失败：{ex.Message}";
        }
    }

    private void UpdateInstalledHint() =>
        InstalledHint.Text = _installed.Contains(GameVersion) ? "（已安装）" : string.Empty;

    private void OnGameVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // 可编辑下拉框：选中的版本要立刻回显在框内
        if (GameVersionBox.SelectedItem is string picked)
        {
            GameVersionBox.Text = picked;
        }

        VersionNameBox.Text = GameVersion;
        UpdateInstalledHint();
        RefreshDependentLists();
    }

    private async void OnLoaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        await RefreshDependentListsAsync();
    }

    private async void OnShaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        await RefreshDependentListsAsync();
    }

    private void RefreshDependentLists() => _ = RefreshDependentListsAsync();

    /// <summary>
    /// 这个游戏版本实际会用哪个 Java（和启动时同一套 PickBest 规则：
    /// 正好声明的那版优先 → 其次"≥ 要求里最小的"；1.13 之前的老版本只认 Java 8）。
    /// 拿不到就返回 null，由调用方退回"声明要求的版本"。
    /// </summary>
    private JavaRuntime? TargetJava(string gameVersion)
    {
        var required = JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, gameVersion);
        return JavaRuntimeService.PickBest(_session.GetJavaRuntimes(), gameVersion, required);
    }

    private static string DescribeJava(JavaRuntime? java, int major) =>
        java is null ? $"Java {major}（尚未安装）" : java.DisplayName;

    /// <summary>刷新加载器版本与光影版本（都要求适配当前游戏版本）。</summary>
    private async Task RefreshDependentListsAsync()
    {
        var gameVersion = GameVersion;
        if (gameVersion.Length == 0)
        {
            return;
        }

        _loading = true;
        try
        {
            // 加载器版本
            var loader = SelectedLoader;
            if (loader == "不安装")
            {
                LoaderVersionBox.ItemsSource = null;
                LoaderVersionBox.IsEnabled = false;
            }
            else
            {
                StatusText.Text = $"正在获取 {loader} 适配 {gameVersion} 的版本…";
                var allVersions = loader switch
                {
                    "Forge" => await _session.Installation.GetForgeVersionsAsync(gameVersion),
                    "NeoForge" => await _session.Installation.GetNeoForgeVersionsAsync(gameVersion),
                    "Quilt" => await _session.Installation.GetQuiltLoaderVersionsAsync(gameVersion),
                    _ => await _session.Installation.GetFabricLoaderVersionsAsync(gameVersion),
                };

                // ★ 适配拦截（用户 2026-10-08）：不显示"装上也跑不起来"的加载器版本。
                //   典型的坑：Fabric 0.16.x 自带 ASM 9.6/9.7，在 Java 25 上 Mixin 会报
                //   Unsupported class file major version 69 → MixinPreProcessorException。
                var targetJava = TargetJava(gameVersion);
                var javaMajor = targetJava?.MajorVersion
                                ?? JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, gameVersion);
                var suited = allVersions
                    .Where(v => LoaderCompatibility.Check(loader, v, gameVersion, javaMajor).Ok)
                    .ToList();
                var hidden = allVersions.Count - suited.Count;

                // 全都不适配时仍然列出来（否则用户没法选），但下面会明确说明原因
                var versions = suited.Count > 0 ? suited : allVersions;
                _unsuitableLoaderReason = suited.Count > 0
                    ? null
                    : LoaderCompatibility.Check(loader, allVersions.FirstOrDefault(), gameVersion, javaMajor).Reason;

                LoaderVersionBox.ItemsSource = versions;
                LoaderVersionBox.IsEnabled = versions.Count > 0;
                LoaderVersionBox.SelectedIndex = versions.Count > 0 ? 0 : -1;
                StatusText.Text = versions.Count == 0
                    ? $"{gameVersion} 没有可用的 {loader} 版本。"
                    : hidden > 0
                        ? $"{loader} 适配 {gameVersion} 的版本 {versions.Count} 个，已隐藏 {hidden} 个在当前 "
                          + $"{DescribeJava(targetJava, javaMajor)} 下会崩的版本。"
                        : $"{loader} 适配 {gameVersion} 的版本 {versions.Count} 个。"
                          + $"（当前 {DescribeJava(targetJava, javaMajor)}，都可用）";
            }

            // 光影版本
            var shader = SelectedShader;
            if (shader == "不安装")
            {
                ShaderVersionBox.ItemsSource = null;
                ShaderVersionBox.IsEnabled = false;
            }
            else if (shader == "Iris")
            {
                // Iris 需要 Fabric / NeoForge；选了 Forge 或"不安装"时按 Fabric 查询并提示
                var irisLoader = loader is "Fabric" or "NeoForge" ? loader : "Fabric";
                var versions = await _mods.GetProjectVersionsAsync("iris", gameVersion, irisLoader);
                ShaderVersionBox.ItemsSource = versions;
                ShaderVersionBox.IsEnabled = versions.Count > 0;
                ShaderVersionBox.SelectedIndex = versions.Count > 0 ? 0 : -1;
                StatusText.Text = versions.Count == 0
                    ? $"Iris 没有适配 {gameVersion} 的版本。"
                    : $"Iris 适配 {gameVersion}（{irisLoader}）的版本 {versions.Count} 个。"
                      + (loader is "Fabric" or "NeoForge" ? string.Empty : " 注意：Iris 需要 Fabric 或 NeoForge，当前加载器不是这两个。");
            }
            else
            {
                // OptiFine 由官网下载页决定版本，这里给一个"自动匹配"的占位
                ShaderVersionBox.ItemsSource = new[] { "自动匹配官网最新" };
                ShaderVersionBox.IsEnabled = true;
                ShaderVersionBox.SelectedIndex = 0;
                StatusText.Text = $"OptiFine 将按 {gameVersion} 从官网自动匹配并下载（安装需手动运行一次安装器）。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取可用版本失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnInstall(object sender, RoutedEventArgs e)
    {
        var gameVersion = GameVersion;
        var targetName = VersionNameBox.Text.Trim();
        if (gameVersion.Length == 0)
        {
            StatusText.Text = "请选择或输入游戏版本。";
            return;
        }

        if (targetName.Length == 0)
        {
            StatusText.Text = "请填写版本名（将作为版本目录名）。";
            return;
        }

        var versionsRoot = Path.Combine(_session.GameRoot, "versions");
        var targetDirectory = Path.Combine(versionsRoot, targetName);
        var overwrite = false;
        if (Directory.Exists(targetDirectory))
        {
            overwrite = MessageWindow.Confirm(
                this,
                "版本已存在",
                $"版本目录「{targetName}」已经存在。\n\n" +
                "是否删除它并重新安装？（原有该版本的存档/模组会一起被删除）\n\n" +
                "只想换个名字的话请点「取消」，然后修改上面的「版本名」。",
                "删除并重装",
                "取消");
            if (!overwrite)
            {
                StatusText.Text = $"请修改版本名后再安装（当前「{targetName}」已存在）。";
                return;
            }
        }

        var loader = SelectedLoader;
        var shader = SelectedShader;
        var loaderVersion = LoaderVersionBox.SelectedItem as string;
        if (loader != "不安装" && string.IsNullOrWhiteSpace(loaderVersion))
        {
            StatusText.Text = $"请选择 {loader} 的版本。";
            return;
        }

        // 适配兜底拦截：正常情况下列表里已经过滤掉了不合适的版本，
        // 只有"一个合适的都没有、只好把全部列出来"时才会走到这里，给出明确原因而不是让它装完再崩。
        if (loader != "不安装" && loaderVersion is { Length: > 0 })
        {
            var javaMajor = TargetJava(gameVersion)?.MajorVersion
                            ?? JavaRuntimeService.RequiredFeatureVersion(_session.GameRoot, gameVersion);
            var check = LoaderCompatibility.Check(loader, loaderVersion, gameVersion, javaMajor);
            if (!check.Ok)
            {
                StatusText.Text = $"这个组合装上也跑不起来：{check.Reason}。\n"
                                  + $"请换一个 {loader} 版本，或到「设置 → 版本与运行时维护」把该版本的 Java 换成合适的。"
                                  + (_unsuitableLoaderReason is { Length: > 0 } ? $"\n（{_unsuitableLoaderReason}）" : string.Empty);
                return;
            }
        }

        // 组装分类步骤：游戏本体 / 加载器 / 光影 —— 交给后台下载管理器执行
        var steps = new List<DownloadStep>
        {
            new(DownloadCategory.游戏本体, $"游戏本体 {gameVersion}"),
        };
        if (loader != "不安装")
        {
            steps.Add(new DownloadStep(DownloadCategory.模组加载器, $"{loader} {loaderVersion}"));
        }

        if (shader != "不安装")
        {
            steps.Add(new DownloadStep(DownloadCategory.光影, shader));
        }

        var loaderText = loader == "不安装" ? "原版" : $"{loader} {loaderVersion}";
        var shaderText = shader == "不安装" ? "无光影" : shader;
        var task = new DownloadTask($"{gameVersion}（{loaderText} · {shaderText}）→ {targetName}", steps);
        DownloadManager.Current.Enqueue(task, async (progress, token) =>
        {
            var (success, message, versionId) = await VersionInstallService.InstallAsync(
                new VersionInstallPlan(gameVersion, targetName, loader, loaderVersion, shader) { Overwrite = overwrite },
                steps, progress, token);
            if (success && !string.IsNullOrWhiteSpace(versionId))
            {
                _installed.Add(versionId!);
            }

            return (success, message);
        });

        InstalledVersionId = targetName;
        _installed.Add(targetName);
        StatusText.Text = "已加入下载队列，可在「资源下载」页或右下角悬浮按钮查看进度。";
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>无边框窗口的标题栏拖动。</summary>
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
