using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using McServerList.Core.Models;

namespace McServerList.App.Views;

/// <summary>
/// 三格信号图标：按信号等级点亮格数并着色；离线（或已下架保留）时画红色叉。
/// 良好=3 格绿、一般=2 格黄、较差=1 格橙、离线/待采样=0 格灰 + 红叉（仅离线）。
/// </summary>
public partial class SignalIcon : UserControl
{
    private static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly Brush FairBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush PoorBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0x92, 0x3C));
    private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x9C, 0xAA, 0xC4));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(string),
        typeof(SignalIcon),
        new PropertyMetadata(StatusLevels.Unknown, OnLevelChanged));

    public SignalIcon()
    {
        InitializeComponent();
        Apply();
    }

    /// <summary>信号等级（good / fair / poor / offline / no-address / unknown）。</summary>
    public string Level
    {
        get => (string)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SignalIcon)d).Apply();

    private void Apply()
    {
        var level = Level ?? StatusLevels.Unknown;
        var lit = level switch
        {
            StatusLevels.Good => 3,
            StatusLevels.Fair => 2,
            StatusLevels.Poor => 1,
            _ => 0,
        };
        var brush = level switch
        {
            StatusLevels.Good => GoodBrush,
            StatusLevels.Fair => FairBrush,
            StatusLevels.Poor => PoorBrush,
            _ => IdleBrush,
        };

        Paint(Bar1, lit >= 1, brush);
        Paint(Bar2, lit >= 2, brush);
        Paint(Bar3, lit >= 3, brush);

        // 离线画红叉；按用户要求「未公开地址」也用同一种图标（0 格灰 + 红叉），
        // 只有"待采样"保持灰格无叉（服务端还没采过样，不代表服务器有问题）。
        OfflineCross.Visibility = level is StatusLevels.Offline or StatusLevels.NoAddress
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void Paint(Rectangle bar, bool on, Brush brush)
    {
        bar.Fill = on ? brush : IdleBrush;
        bar.Opacity = on ? 1 : 0.28;
    }
}
