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
        ["Settings"] = "M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 M12,3 L13,5.2 15.5,5.8 17.5,4.5 19.5,6.5 18.2,8.5 18.8,11 21,12 18.8,13 18.2,15.5 19.5,17.5 17.5,19.5 15.5,18.2 13,18.8 12,21 11,18.8 8.5,18.2 6.5,19.5 4.5,17.5 5.8,15.5 5.2,13 3,12 5.2,11 5.8,8.5 4.5,6.5 6.5,4.5 8.5,5.8 11,5.2 Z",
        ["Search"] = "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 M15.2,15.2 L21,21",
        ["Close"] = "M5,5 L19,19 M19,5 L5,19",
        ["Browser"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M3.5,9 L20.5,9 M4.5,15 L19.5,15 M12,3 C9,7 9,17 12,21 M12,3 C15,7 15,17 12,21",
        ["Code"] = "M9,7 L4,12 9,17 M15,7 L20,12 15,17 M13,5 L11,19",
        ["Terminal"] = "M4,5 L20,5 20,19 4,19 Z M7,9 L10,12 7,15 M12,15 L17,15",
        ["File"] = "M6,3 L14,3 19,8 19,21 6,21 Z M14,3 L14,8 19,8 M9,12 L16,12 M9,16 L16,16",
        ["TextFile"] = "M6,3 L14,3 19,8 19,21 6,21 Z M14,3 L14,8 19,8 M9,12 L16,12 M9,15 L16,15 M9,18 L14,18",
        ["App"] = "M4,4 L10,4 10,10 4,10 Z M14,4 L20,4 20,10 14,10 Z M4,14 L10,14 10,20 4,20 Z M14,14 L20,14 20,20 14,20 Z",
        ["ArrowRight"] = "M5,12 L19,12 M14,7 L19,12 14,17",
        ["User"] = "M12,4 A4,4 0 1 0 12,12 A4,4 0 1 0 12,4 M5,21 C5.5,16 18.5,16 19,21",
        ["Refresh"] = "M20,8 L20,3 15,3 M20,3 C16,0.5 9,1.5 5.5,6 C2,10.5 3,16 7,19 M4,16 L4,21 9,21 M4,21 C8,23.5 15,22.5 18.5,18 C22,13.5 21,8 17,5",
        ["New"] = "M12,5 L12,19 M5,12 L19,12",
        ["Display"] = "M3,4 L21,4 21,17 3,17 Z M8,21 L16,21 M12,17 L12,21 M7,8 L17,8",
        ["Personalize"] = "M12,4 A8,8 0 1 0 12,20 C14,20 14,17 16,17 L18,17 C20,17 21,15 20,13 C19,11 17,11 16,12 M8,8 L8,8.1 M12,7 L12,7.1 M16,8 L16,8.1 M7,13 L7,13.1",
        ["OpenFolder"] = "M3,8 L9,8 11,10 21,10 19,20 3,20 Z M3,8 L3,6 9,6 11,8 M8,14 L16,14 M13,11 L16,14 13,17",
        ["Properties"] = "M6,3 L16,3 20,7 20,21 6,21 Z M16,3 L16,7 20,7 M9,11 L17,11 M9,15 L17,15 M9,19 L14,19",
        ["Copy"] = "M8,8 L20,8 20,20 8,20 Z M4,4 L16,4 16,8 M4,4 L4,16 8,16",
        ["Rename"] = "M5,18 L8,18 18,8 15,5 5,15 Z M14,6 L17,9 M4,21 L20,21",
        ["Delete"] = "M5,7 L19,7 M9,7 L9,4 15,4 15,7 M7,7 L8,21 16,21 17,7 M10,11 L10,17 M14,11 L14,17",
        ["Clipboard"] = "M8,5 L8,3 16,3 16,5 M7,5 L17,5 19,7 19,21 5,21 5,7 Z M9,10 L15,10 M9,14 L15,14 M9,18 L13,18",
        ["Sort"] = "M5,6 L19,6 M5,12 L15,12 M5,18 L11,18 M18,10 L18,20 M15,17 L18,20 21,17",
        ["View"] = "M3,12 C7,5 17,5 21,12 C17,19 7,19 3,12 M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5"
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
        if (!Paths.TryGetValue(kind, out var data)) data = Paths["App"];
        var geometry = Geometry.Parse(data);
        if (geometry.CanFreeze) geometry.Freeze();
        IconPath.Data = geometry;
    }
}
