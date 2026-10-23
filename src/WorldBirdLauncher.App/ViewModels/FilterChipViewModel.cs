using WorldBirdLauncher.App.Infrastructure;

namespace WorldBirdLauncher.App.ViewModels;

/// <summary>筛选标签（可多选）。</summary>
public sealed class FilterChipViewModel : ObservableObject
{
    private bool _isChecked;
    private bool _isVisible = true;

    public FilterChipViewModel(string label, Action onChanged)
    {
        Label = label;
        OnChanged = onChanged;
    }

    public string Label { get; }

    private Action OnChanged { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                OnChanged();
            }
        }
    }

    /// <summary>是否显示（版本/玩法默认只显示常用项，点「更多》」展开）。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
            {
                return;
            }

            _isVisible = value;
            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(Visibility));
        }
    }

    public System.Windows.Visibility Visibility =>
        IsVisible ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
}
