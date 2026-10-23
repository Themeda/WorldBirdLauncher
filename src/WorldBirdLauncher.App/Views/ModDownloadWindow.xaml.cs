using System.Diagnostics;
using System.Windows;
using WorldBirdLauncher.App.Services;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Views;

/// <summary>
/// 「下载模组」弹窗：Modrinth 搜索 → 按目标版本的加载器匹配 → 下载到该版本的 mods 目录。
/// </summary>
public partial class ModDownloadWindow : Window
{
    private readonly AppSession _session = AppSession.Current;
    private readonly ModDownloadService _mods = new();
    private bool _busy;

    public ModDownloadWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadTargets();
    }

    private void LoadTargets()
    {
        var installed = _session.Installation.GetInstalledVersions(_session.GameRoot).Where(v => v.IsUsable).Select(v => v.Id).ToList();
        TargetVersionBox.ItemsSource = installed;
        if (installed.Count > 0)
        {
            var index = installed.IndexOf(_session.CurrentVersionId ?? string.Empty);
            TargetVersionBox.SelectedIndex = index >= 0 ? index : 0;
        }

        StatusText.Text = installed.Count == 0
            ? "本机还没有已安装的版本，请先到「安装版本…」装一个。"
            : $"目标版本共 {installed.Count} 个。";
    }

    private string? TargetVersion => TargetVersionBox.SelectedItem as string;

    /// <summary>下载类型：mod（模组）或 shader（光影包）。</summary>
    private string ProjectType => (KindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() == "光影包"
        ? "shader"
        : "mod";

    /// <summary>目标目录：模组进 mods，光影包进 shaderpacks（版本隔离时装在版本目录里）。</summary>
    private string ResolveModsDirectory(string versionId)
    {
        var folder = ProjectType == "shader" ? "shaderpacks" : "mods";
        return _session.Settings.VersionIsolation
            ? Path.Combine(_session.GameRoot, "versions", versionId, folder)
            : Path.Combine(_session.GameRoot, folder);
    }

    /// <summary>目标版本的加载器：优先读版本 JSON 的库信息，读不到再按 id 猜。</summary>
    private string ResolveLoader(string versionId)
    {
        try
        {
            var jsonPath = Path.Combine(_session.GameRoot, "versions", versionId, versionId + ".json");
            if (File.Exists(jsonPath))
            {
                var text = File.ReadAllText(jsonPath);
                if (text.Contains("net.neoforged", StringComparison.OrdinalIgnoreCase))
                {
                    return "NeoForge";
                }

                if (text.Contains("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
                    || versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
                {
                    return "Forge";
                }

                if (text.Contains("net.fabricmc", StringComparison.OrdinalIgnoreCase)
                    || versionId.Contains("fabric", StringComparison.OrdinalIgnoreCase))
                {
                    return "Fabric";
                }
            }
        }
        catch (Exception)
        {
            // 猜不出来就当原版
        }

        return "原版";
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var keyword = SearchBox.Text.Trim();
        if (keyword.Length == 0)
        {
            StatusText.Text = "请输入要搜索的模组名称。";
            return;
        }

        if (_busy)
        {
            return;
        }

        _busy = true;
        StatusText.Text = $"正在搜索「{keyword}」…";
        try
        {
            var version = TargetVersion;
            var loader = version is null ? null : ResolveLoader(version);
            var results = await _mods.SearchAsync(keyword, version, loader, ProjectType);
            ResultList.ItemsSource = results;
            StatusText.Text = results.Count == 0
                ? $"没有找到匹配的{(ProjectType == "shader" ? "光影包" : "模组")}。"
                : $"找到 {results.Count} 个{(ProjectType == "shader" ? "光影包" : "模组")}（版本 {version}"
                  + (ProjectType == "shader" ? "）" : $" · 加载器 {loader}）");
        }
        catch (Exception ex)
        {
            StatusText.Text = $"搜索失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_busy || ResultList.SelectedItem is not ModSearchResult mod)
        {
            StatusText.Text = "请先在列表里选中一个模组。";
            return;
        }

        var version = TargetVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            StatusText.Text = "请先选择目标版本。";
            return;
        }

        _busy = true;
        DownloadButton.IsEnabled = false;
        try
        {
            var loader = ResolveLoader(version!);
            var directory = ResolveModsDirectory(version!);
            var category = ProjectType == "shader" ? DownloadCategory.光影 : DownloadCategory.模组;
            var label = ProjectType == "shader" ? "光影包" : "模组";
            var steps = new List<DownloadStep> { new(category, $"{label}：{mod.Title}") };
            var task = new DownloadTask($"{label} {mod.Title} → {version}", steps);
            DownloadManager.Current.Enqueue(task, async (progress, token) =>
            {
                steps[0].Status = DownloadStatus.下载中;
                var result = await _mods.DownloadAsync(mod.ProjectId, version!, loader, directory, progress, token);
                return (result.Success, result.Message);
            });

            StatusText.Text = "已加入下载队列，可在「资源下载」页查看进度。";
        }
        finally
        {
            _busy = false;
            DownloadButton.IsEnabled = true;
        }
    }

    private void OnOpenMods(object sender, RoutedEventArgs e)
    {
        var version = TargetVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        var directory = ResolveModsDirectory(version!);
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>无边框窗口的标题栏拖动。</summary>
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
