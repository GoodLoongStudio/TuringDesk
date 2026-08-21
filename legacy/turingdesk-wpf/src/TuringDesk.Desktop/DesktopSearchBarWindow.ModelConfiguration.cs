using TuringDesk.Desktop.Services;

namespace TuringDesk.Desktop;

public partial class DesktopSearchBarWindow
{
    internal void ReloadModelChoicesFromConfiguration()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(ReloadModelChoicesFromConfiguration));
            return;
        }

        RefreshModelChoices();
    }
}
