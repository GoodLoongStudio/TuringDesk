using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public sealed class NativeShellIcon : Image
{
    public static readonly DependencyProperty StockIconProperty = DependencyProperty.Register(
        nameof(StockIcon),
        typeof(ShellStockIconId),
        typeof(NativeShellIcon),
        new PropertyMetadata(ShellStockIconId.Application, OnIconPropertyChanged));

    public static readonly DependencyProperty LargeProperty = DependencyProperty.Register(
        nameof(Large),
        typeof(bool),
        typeof(NativeShellIcon),
        new PropertyMetadata(false, OnIconPropertyChanged));

    public ShellStockIconId StockIcon
    {
        get => (ShellStockIconId)GetValue(StockIconProperty);
        set => SetValue(StockIconProperty, value);
    }

    public bool Large
    {
        get => (bool)GetValue(LargeProperty);
        set => SetValue(LargeProperty, value);
    }

    public NativeShellIcon()
    {
        Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        Loaded += (_, _) => RefreshIcon();
    }

    private static void OnIconPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is NativeShellIcon icon) icon.RefreshIcon();
    }

    private void RefreshIcon()
    {
        Source = ShellIconService.GetStockIcon(StockIcon, Large);
    }
}
