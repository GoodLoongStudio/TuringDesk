namespace TuringDesk.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// Entry point used by desktop-native lightweight surfaces such as the AI Orb.
    /// It intentionally reuses the exact same Runtime/Harness path and floating
    /// cards as the control-center command box instead of creating another agent.
    /// </summary>
    internal async Task SubmitExternalCommandAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => SubmitExternalCommandAsync(text)).Task.Unwrap();
            return;
        }

        CommandBox.Text = text.Trim();
        await SubmitCommandAsync();
    }

    internal void RequestApplicationExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(RequestApplicationExit);
            return;
        }

        ShellSession.ExitRequested = true;
        Close();
    }
}
