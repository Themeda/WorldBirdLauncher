using System.Windows;
using System.Windows.Input;
using WorldBirdLauncher.App.Services;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 直连启动发现"目标版本本机没有"时的确认窗：预填好版本 / 加载器 / 光影，允许改，
/// 用户点「下载并启动」后由调用方入队安装（原版 → 加载器 → 光影），装完再启动进服。
/// </summary>
public partial class AutoInstallWindow : Window
{
    private readonly IReadOnlyList<string> _gameVersions;
    private bool _loading;

    public AutoInstallWindow(
        Window? owner,
        string serverName,
        string supported,
        IReadOnlyList<string> gameVersions,
        string defaultGameVersion,
        string defaultLoader,
        string defaultShader,
        string? note)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        _gameVersions = gameVersions;
        MessageText.Text =
            $"服务器「{serverName}」支持 {supported}，本机还没有匹配的版本。\n" +
            "确认下面这些内容后点「下载并启动」，装完会自动切换版本并进入服务器："
            + (string.IsNullOrWhiteSpace(note) ? string.Empty : $"\n\n注意：{note}");

        LoaderBox.ItemsSource = new[] { VersionInstallPlan.NoLoader, "Forge", "Fabric" };
        ShaderBox.ItemsSource = new[] { VersionInstallPlan.NoShader, "Iris", "OptiFine" };
        GameVersionBox.ItemsSource = gameVersions;

        _loading = true;
        GameVersionBox.SelectedItem = gameVersions.FirstOrDefault(v => v == defaultGameVersion) ?? gameVersions.FirstOrDefault();
        LoaderBox.SelectedItem = defaultLoader;
        ShaderBox.SelectedItem = defaultShader;
        _loading = false;
        _ = RefreshLoaderVersionsAsync();
        UpdatePlanText();
    }

    /// <summary>用户确认后的安装计划；取消时为 null。</summary>
    public VersionInstallPlan? Plan { get; private set; }

    private string? SelectedGameVersion => GameVersionBox.SelectedItem as string;

    private string SelectedLoader => LoaderBox.SelectedItem as string ?? VersionInstallPlan.NoLoader;

    private string SelectedShader => ShaderBox.SelectedItem as string ?? VersionInstallPlan.NoShader;

    private async void OnGameVersionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        await RefreshLoaderVersionsAsync();
        UpdatePlanText();
    }

    private async void OnLoaderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        await RefreshLoaderVersionsAsync();
        // 光影默认跟着加载器走：Fabric/NeoForge 用 Iris，原版/Forge 用 OptiFine
        if (ShaderBox.SelectedItem is string shader && shader != VersionInstallPlan.NoShader)
        {
            var expected = SelectedLoader is "Fabric" or "NeoForge" ? "Iris" : "OptiFine";
            if (shader != expected)
            {
                ShaderBox.SelectedItem = expected;
            }
        }

        UpdatePlanText();
    }

    private void OnShaderChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdatePlanText();
        }
    }

    private async Task RefreshLoaderVersionsAsync()
    {
        var gameVersion = SelectedGameVersion;
        var loader = SelectedLoader;
        if (string.IsNullOrWhiteSpace(gameVersion) || loader == VersionInstallPlan.NoLoader)
        {
            LoaderVersionBox.ItemsSource = null;
            LoaderVersionBox.IsEnabled = false;
            UpdatePlanText();
            return;
        }

        var installation = AppSession.Current.Installation;
        LoaderVersionBox.IsEnabled = false;
        try
        {
            var versions = loader == "Forge"
                ? await installation.GetForgeVersionsAsync(gameVersion!)
                : await installation.GetFabricLoaderVersionsAsync(gameVersion!);
            LoaderVersionBox.ItemsSource = versions;
            LoaderVersionBox.IsEnabled = versions.Count > 0;
            LoaderVersionBox.SelectedIndex = versions.Count > 0 ? 0 : -1;
        }
        catch (Exception)
        {
            LoaderVersionBox.ItemsSource = null;
        }

        UpdatePlanText();
    }

    private void UpdatePlanText()
    {
        var gameVersion = SelectedGameVersion ?? "?";
        var loader = SelectedLoader;
        var shader = SelectedShader;
        var loaderVersion = LoaderVersionBox.SelectedItem as string;
        var loaderText = loader == VersionInstallPlan.NoLoader
            ? "原版"
            : $"{loader} {loaderVersion ?? "（正在获取版本…）"}";
        var shaderText = shader == VersionInstallPlan.NoShader ? "不装光影" : shader;
        PlanText.Text = $"将安装：{gameVersion}（{loaderText} · {shaderText}）";
        InstallButton.IsEnabled = !string.IsNullOrWhiteSpace(gameVersion)
                                  && (loader == VersionInstallPlan.NoLoader || !string.IsNullOrWhiteSpace(loaderVersion));
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var gameVersion = SelectedGameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return;
        }

        var loader = SelectedLoader;
        var shader = SelectedShader;
        var targetName = BuildTargetName(gameVersion!, loader, shader);
        Plan = new VersionInstallPlan(gameVersion!, targetName, loader, LoaderVersionBox.SelectedItem as string, shader);
        DialogResult = true;
        Close();
    }

    /// <summary>直连启动的默认目录名：<游戏版本>[-加载器][-光影]，碰撞时安装流程会自动加序号。</summary>
    private static string BuildTargetName(string gameVersion, string loader, string shader)
    {
        var name = gameVersion;
        if (loader != VersionInstallPlan.NoLoader)
        {
            name += "-" + loader;
        }

        if (shader != VersionInstallPlan.NoShader)
        {
            name += "-" + shader;
        }

        return name;
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
