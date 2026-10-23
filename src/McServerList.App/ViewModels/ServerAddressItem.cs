using McServerList.App.Infrastructure;
using McServerList.Core.Models;

namespace McServerList.App.ViewModels;

/// <summary>详情页「服务器地址」里的一行：一条线路（主线路 / 备用 / 联通 …）。</summary>
public sealed class ServerAddressItem : ObservableObject
{
    private string _statusText = "未检测";
    private bool _isPreferred;

    public ServerAddressItem(string serverId, ServerAddress address, bool isPrimary, bool isPreferred)
    {
        ServerId = serverId;
        Label = address.Label;
        Host = address.Host;
        Port = address.Port;
        IsPrimary = isPrimary;
        _isPreferred = isPreferred;
    }

    public string ServerId { get; }

    public string Label { get; }

    public string Host { get; }

    public int Port { get; }

    public bool IsPrimary { get; }

    public string EndPoint => Port == 25565 ? Host : $"{Host}:{Port}";

    public string LabelText => IsPrimary ? $"{Label}（主线路）" : Label;

    /// <summary>单条线路的探测结果（仅在详情页手动点「Ping」时更新）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>是否被用户选为"直连启动"使用的线路。</summary>
    public bool IsPreferred
    {
        get => _isPreferred;
        set
        {
            if (SetProperty(ref _isPreferred, value))
            {
                OnPropertyChanged(nameof(PreferredVisibility));
                OnPropertyChanged(nameof(NotPreferredVisibility));
            }
        }
    }

    /// <summary>已选中：显示实心的"当前直连"按钮。</summary>
    public System.Windows.Visibility PreferredVisibility =>
        IsPreferred ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    /// <summary>未选中：显示描边的"设为直连"按钮（去掉原来的 ✓ 文案）。</summary>
    public System.Windows.Visibility NotPreferredVisibility =>
        IsPreferred ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}
