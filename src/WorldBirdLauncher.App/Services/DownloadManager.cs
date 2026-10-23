using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using WorldBirdLauncher.App.Infrastructure;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Services;

/// <summary>下载分类：对应下载管理界面里的四类进度。</summary>
public enum DownloadCategory
{
    游戏本体,
    模组加载器,
    光影,
    模组,
}

public enum DownloadStatus
{
    排队中,
    下载中,
    已完成,
    失败,
    已取消,
}

/// <summary>下载任务里的一个步骤（例如"游戏本体""加载器"），用于分类进度展示。</summary>
public sealed class DownloadStep : ObservableObject
{
    private double _percentage;
    private DownloadStatus _status = DownloadStatus.排队中;
    private string? _currentFile;
    private string? _fileProgressText;
    private bool _isIndeterminate;
    private string? _stageText;

    public DownloadStep(DownloadCategory category, string title)
    {
        Category = category;
        Title = title;
    }

    public DownloadCategory Category { get; }

    public string Title { get; }

    public double Percentage
    {
        get => _percentage;
        set
        {
            if (SetProperty(ref _percentage, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>当前正在写入的文件（相对 .minecraft 的路径）。</summary>
    public string? CurrentFile
    {
        get => _currentFile;
        set => SetProperty(ref _currentFile, value);
    }

    /// <summary>当前文件的"已下 / 总大小"。</summary>
    public string? FileProgressText
    {
        get => _fileProgressText;
        set
        {
            if (SetProperty(ref _fileProgressText, value))
            {
                OnPropertyChanged(nameof(CurrentFileText));
            }
        }
    }

    /// <summary>进度不可估算（长时间没有文件写入，例如 OptiFine 打补丁）→ 界面用活动动画。</summary>
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => SetProperty(ref _isIndeterminate, value);
    }

    public string CurrentFileText => CurrentFile is null
        ? string.Empty
        : FileProgressText is { Length: > 0 }
            ? $"当前文件：{CurrentFile}（{FileProgressText}）"
            : $"当前文件：{CurrentFile}";

    /// <summary>阶段文案（如「下载中 · 63.18KB/s（707/708 个文件）」）。</summary>
    public string? StageText
    {
        get => _stageText;
        set => SetProperty(ref _stageText, value);
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsDone => Status is DownloadStatus.已完成;

    public string StatusText => Status switch
    {
        DownloadStatus.已完成 => "✓ 完成",
        DownloadStatus.失败 => "失败",
        DownloadStatus.已取消 => "已取消",
        DownloadStatus.下载中 => $"{Percentage:0}%",
        _ => "等待中",
    };
}

/// <summary>一个下载任务（一次安装或一次模组下载）。</summary>
public sealed class CategoryProgress : ObservableObject
{
    private double _percentage;
    private int _total;
    private int _done;
    private string? _currentFileText;
    private bool _isIndeterminate;
    private string? _stageText;
    private bool _hasActive;

    public CategoryProgress(DownloadCategory category) => Category = category;

    public DownloadCategory Category { get; }

    public string Name => Category switch
    {
        DownloadCategory.游戏本体 => "游戏本体",
        DownloadCategory.模组加载器 => "模组加载器",
        DownloadCategory.光影 => "光影",
        _ => "模组",
    };

    public double Percentage
    {
        get => _percentage;
        private set => SetProperty(ref _percentage, value);
    }

    public int Total
    {
        get => _total;
        private set
        {
            if (SetProperty(ref _total, value))
            {
                OnPropertyChanged(nameof(IsVisible));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public int Done
    {
        get => _done;
        private set
        {
            if (SetProperty(ref _done, value))
            {
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(DetailText));
            }
        }
    }

    public bool IsVisible => Total > 0;

    public bool IsDone => Total > 0 && Done >= Total;

    /// <summary>当前正在下载的文件（含单文件进度），没有活跃任务时为空。</summary>
    public string? CurrentFileText
    {
        get => _currentFileText;
        private set
        {
            if (SetProperty(ref _currentFileText, value))
            {
                OnPropertyChanged(nameof(HasCurrentFile));
            }
        }
    }

    public bool HasCurrentFile => !string.IsNullOrEmpty(CurrentFileText);

    /// <summary>当前阶段文案（右侧显示的「下载中 · 速度（x/y 个文件）」）。</summary>
    public string? StageText
    {
        get => _stageText;
        private set => SetProperty(ref _stageText, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => SetProperty(ref _isIndeterminate, value);
    }

    public string DetailText => Total == 0
        ? "暂无任务"
        : IsDone
            ? "✓ 已完成"
            : !_hasActive
                ? $"等待中（{Total} 项）"
                : $"{Done}/{Total} 完成（{Percentage:0}%）";

    internal void Update(double percentage, int total, int done)
    {
        Percentage = percentage;
        Total = total;
        Done = done;
    }

    internal void UpdateFile(string? currentFileText, bool isIndeterminate)
    {
        CurrentFileText = currentFileText;
        IsIndeterminate = isIndeterminate;
    }

    internal void UpdateStage(string? stageText, bool hasActive)
    {
        StageText = stageText;
        if (_hasActive != hasActive)
        {
            _hasActive = hasActive;
            OnPropertyChanged(nameof(DetailText));
        }
    }
}

/// <summary>一个下载任务（一次安装或一次模组下载）。</summary>
public sealed class DownloadTask : ObservableObject
{
    private DownloadStatus _status = DownloadStatus.排队中;
    private string _message = string.Empty;

    public DownloadTask(string title, IEnumerable<DownloadStep> steps)
    {
        Title = title;
        Steps = new ObservableCollection<DownloadStep>(steps);
    }

    public string Title { get; }

    public ObservableCollection<DownloadStep> Steps { get; }

    public CancellationTokenSource Cancellation { get; } = new();

    internal readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 任务完成的等待点（成功 true / 失败或取消 false）。
    /// 供"下载完成后自动切换版本并进服"这类流程 await；界面进度照旧走 <see cref="Status"/>。
    /// </summary>
    public Task<bool> Completion => _completion.Task;

    internal Func<IProgress<InstallProgress>, CancellationToken, Task<(bool Success, string Message)>>? Work { get; set; }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsFinished));
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public double Percentage => Steps.Count == 0 ? 0 : Steps.Average(s => s.Percentage);

    public bool IsFinished => Status is DownloadStatus.已完成 or DownloadStatus.失败 or DownloadStatus.已取消;

    public string StatusText => Status switch
    {
        DownloadStatus.排队中 => "排队中",
        DownloadStatus.下载中 => $"{Percentage:0}%",
        DownloadStatus.已完成 => "已完成",
        DownloadStatus.失败 => "失败",
        _ => "已取消",
    };

    /// <summary>当前正在下载的步骤里的"当前文件"（任务卡片左侧显示）。</summary>
    public string? CurrentFileText =>
        Steps.FirstOrDefault(s => s.Status == DownloadStatus.下载中)?.CurrentFileText;

    /// <summary>当前正在下载的步骤的阶段文案（任务卡片右侧显示）。</summary>
    public string? StageText =>
        Steps.FirstOrDefault(s => s.Status == DownloadStatus.下载中)?.StageText;

    internal void NotifyProgress()
    {
        OnPropertyChanged(nameof(Percentage));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CurrentFileText));
        OnPropertyChanged(nameof(StageText));
    }
}

/// <summary>
/// 应用级下载管理器：任务排队执行，并发数由设置决定（默认 2 个并行）。
/// 支持后台下载——窗口关掉也继续跑，主窗口右下角悬浮按钮显示总进度。
/// </summary>
public sealed class DownloadManager : ObservableObject
{
    /// <summary>某一类（游戏本体/加载器/光影/模组）的汇总进度。</summary>
   private readonly Queue<DownloadTask> _queue = new();
    private int _running;

    public static DownloadManager Current { get; } = new();

    public DownloadManager() => Categories = [GameCore, Loader, Shader, Mod];

    public ObservableCollection<DownloadTask> Tasks { get; } = [];

    /// <summary>四类下载的分类进度（供下载管理界面显示进度条与完成打勾）。</summary>
    public CategoryProgress GameCore { get; } = new(DownloadCategory.游戏本体);

    public CategoryProgress Loader { get; } = new(DownloadCategory.模组加载器);

    public CategoryProgress Shader { get; } = new(DownloadCategory.光影);

    public CategoryProgress Mod { get; } = new(DownloadCategory.模组);

    /// <summary>四类进度（供下载页按分类渲染）。</summary>
    public IReadOnlyList<CategoryProgress> Categories { get; }

    /// <summary>全部任务结束时触发（用于右下角气泡通知）。</summary>
    public event Action? AllCompleted;

    public int MaxConcurrency
    {
        get
        {
            var value = AppSession.Current.Settings.MaxConcurrentDownloads;
            return NetCompat.Clamp(value <= 0 ? 2 : value, 1, 6);
        }
    }

    public bool HasActive => Tasks.Any(t => !t.IsFinished);

    public bool IsEmpty => Tasks.Count == 0;

    public double OverallPercentage => Tasks.Count == 0
        ? 0
        : Tasks.Where(t => !t.IsFinished).Select(t => t.Percentage).DefaultIfEmpty(100).Average();

    public string OverallText => $"{Tasks.Count(t => t.IsFinished)}/{Tasks.Count} 完成";

    public void Enqueue(DownloadTask task,
        Func<IProgress<InstallProgress>, CancellationToken, Task<(bool Success, string Message)>> work)
    {
        task.Work = work;
        Tasks.Add(task);
        _queue.Enqueue(task);
        RaiseAggregate();
        Pump();
    }

    public void Cancel(DownloadTask task)
    {
        task.Cancellation.Cancel();
        if (task.Status == DownloadStatus.排队中)
        {
            task.Status = DownloadStatus.已取消;
            task._completion.TrySetResult(false);
            RaiseAggregate();
        }
    }

    public void ClearFinished()
    {
        foreach (var task in Tasks.Where(t => t.IsFinished).ToList())
        {
            Tasks.Remove(task);
        }

        RaiseAggregate();
    }

    /// <summary>并发数调整后立即按新上限调度（原来在排队的任务会马上开跑）。</summary>
    public void PumpIfIdle() => Pump();

    private void Pump()
    {
        while (_running < MaxConcurrency && _queue.Count > 0)
        {
            var task = _queue.Dequeue();
            if (task.Status == DownloadStatus.已取消)
            {
                continue;
            }

            _running++;
            _ = RunAsync(task);
        }
    }

    private async Task RunAsync(DownloadTask task)
    {
        task.Status = DownloadStatus.下载中;
        RaiseAggregate();
        var progress = new Progress<InstallProgress>(p =>
        {
            var step = task.Steps.FirstOrDefault(s => s.Status == DownloadStatus.下载中);
            if (step is not null)
            {
                if (p.Percentage >= 0)
                {
                    step.IsIndeterminate = false;
                    step.Percentage = p.Percentage;
                }
                else
                {
                    step.IsIndeterminate = true; // 例如 OptiFine 打补丁：没有可估算的进度
                }

                step.CurrentFile = p.File;
                step.FileProgressText = FormatFileProgress(p);
                step.StageText = p.Stage;
            }

            if (!string.IsNullOrWhiteSpace(p.Stage))
            {
                task.Message = p.Detail is null ? p.Stage : $"{p.Stage} · {p.Detail}";
            }

            task.NotifyProgress();
            RaiseAggregate();
        });

        try
        {
            var (success, message) = await task.Work!(progress, task.Cancellation.Token);
            task.Status = success ? DownloadStatus.已完成 : DownloadStatus.失败;
            task.Message = message;
            foreach (var step in task.Steps)
            {
                if (success)
                {
                    step.Status = DownloadStatus.已完成;
                    step.Percentage = 100;
                    step.CurrentFile = null;
                    step.StageText = null;
                }
                else if (step.Status == DownloadStatus.下载中)
                {
                    step.Status = DownloadStatus.失败;
                }
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.已取消;
            task.Message = "已取消";
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.失败;
            task.Message = ex.Message;
            App.LogLine($"后台下载失败：{task.Title} → {ex}");
        }
        finally
        {
            _running--;
            task._completion.TrySetResult(task.Status == DownloadStatus.已完成);
            task.NotifyProgress();
            RaiseAggregate();
            Pump();

            if (!HasActive)
            {
                AllCompleted?.Invoke();
                RaiseAggregate();
            }
        }
    }

    private void RaiseAggregate()
    {
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(OverallPercentage));
        OnPropertyChanged(nameof(OverallText));

        // 分类进度：把每个步骤按类别汇总
        foreach (var category in (DownloadCategory[])[DownloadCategory.游戏本体, DownloadCategory.模组加载器, DownloadCategory.光影, DownloadCategory.模组])
        {
            var steps = Tasks.SelectMany(t => t.Steps).Where(s => s.Category == category).ToList();
            var target = category switch
            {
                DownloadCategory.游戏本体 => GameCore,
                DownloadCategory.模组加载器 => Loader,
                DownloadCategory.光影 => Shader,
                _ => Mod,
            };
            target.Update(
                steps.Count == 0 ? 0 : steps.Average(s => s.Percentage),
                steps.Count,
                steps.Count(s => s.Status == DownloadStatus.已完成));

            var active = steps.FirstOrDefault(s => s.Status == DownloadStatus.下载中);
            target.UpdateFile(
                active?.CurrentFileText is { Length: > 0 } text ? text : null,
                active?.IsIndeterminate == true);
            target.UpdateStage(active?.StageText, active is not null);
        }
    }

    private static string? FormatFileProgress(InstallProgress progress)
    {
        if (progress.File is null)
        {
            return progress.Detail;
        }

        // 大文件下载时补上速度与剩余时间（用户反馈"看着像卡死"）
        return ProgressFormat.Describe(progress);
    }

    private static string FormatSize(long bytes) => ProgressFormat.Size(bytes);
}
