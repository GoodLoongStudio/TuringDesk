using System.Windows.Controls;

namespace TuringDesk.Desktop;

public partial class DesktopLibraryWindow
{
    private bool _profilesTabAdded;

    private void EnsureProfilesTab()
    {
        if (_profilesTabAdded) return;
        _profilesTabAdded = true;

        var profileTab = new TabItem
        {
            Header = "多屏配置",
            Content = new MonitorProfilesControl()
        };

        // Keep profiles close to playlists, before app rules/performance.
        var index = Math.Min(2, Tabs.Items.Count);
        Tabs.Items.Insert(index, profileTab);
    }
}
