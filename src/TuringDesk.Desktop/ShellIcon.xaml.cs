using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TuringDesk.Desktop;

public partial class ShellIcon : UserControl
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(string),
        typeof(ShellIcon),
        new PropertyMetadata("Agent", OnKindChanged));

    private static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Start"] = "M3,3 L10,3 10,10 3,10 Z M14,3 L21,3 21,10 14,10 Z M3,14 L10,14 10,21 3,21 Z M14,14 L21,14 21,21 14,21 Z",
        ["Desktop"] = "M3,4 L21,4 21,17 3,17 Z M8,21 L16,21 M12,17 L12,21",
        ["Agent"] = "M12,2 L14.2,8.2 20.5,10.5 14.2,12.8 12,19 9.8,12.8 3.5,10.5 9.8,8.2 Z M18.5,3.5 L19.3,5.7 21.5,6.5 19.3,7.3 18.5,9.5 17.7,7.3 15.5,6.5 17.7,5.7 Z",
        ["Tasks"] = "M5,5 L19,5 19,17 5,17 Z M2,8 L5,8 M2,8 L2,20 16,20 M16,17 L16,20",
        ["Network"] = "M3,9 C8,4 16,4 21,9 M6,12 C10,8 14,8 18,12 M9,15 C11,13 13,13 15,15 M12,19 L12,19.1",
        ["Volume"] = "M4,10 L8,10 13,6 13,18 8,14 4,14 Z M16,9 C18,11 18,13 16,15 M18,6 C22,10 22,14 18,18",
        ["Notification"] = "M6,17 L18,17 M8,17 L8,10 C8,5 16,5 16,10 L16,17 M10,20 C11,21 13,21 14,20",
        ["Power"] = "M12,3 L12,11 M7,6 C3,9 3,16 7,19 C10,22 15,22 18,19 C22,15 21,9 17,6",
        ["Folder"] = "M3,7 L9,7 11,9 21,9 21,20 3,20 Z M3,7 L3,5 9,5 11,7",
        ["Settings"] = "M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 M12,3 L13,5.2 15.5,5.8 17.5,4.5 19.5,6.5 18.2,8.5 18.8,11 21,12 18.8,13 18.2,15.5 19.5,17.5 17.5,19.5 15.5,18.2 13,18.8 12,21 11,18.8 8.5,18.2 6.5,19.5 4.5,17.5 5.8,15.5 5.2,13 3,12 5.2,11 5.8,8.5 4.5,6.5 6.5,4.5 8.5,5.8 11,5.2 Z"
    };

    public ShellIcon()
    {
        InitializeComponent();
        ApplyKind(Kind);
    }

    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ShellIcon icon)
        {
            icon.ApplyKind(e.NewValue as string ?? "Agent");
        }
    }

    private void ApplyKind(string kind)
    {
        if (!Paths.TryGetValue(kind, out var data)) data = Paths["Agent"];
        var geometry = Geometry.Parse(data);
        if (geometry.CanFreeze) geometry.Freeze();
        IconPath.Data = geometry;
    }
}
