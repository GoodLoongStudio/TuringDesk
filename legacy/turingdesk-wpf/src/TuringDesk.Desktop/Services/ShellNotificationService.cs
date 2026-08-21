namespace TuringDesk.Desktop.Services;

public sealed record ShellNotification(
    Guid Id,
    DateTimeOffset Timestamp,
    string Title,
    string Message,
    string Kind);

public static class ShellNotificationService
{
    private static readonly object Gate = new();
    private static readonly List<ShellNotification> Items = new();

    public static event Action? Changed;

    public static IReadOnlyList<ShellNotification> Snapshot()
    {
        lock (Gate)
        {
            return Items
                .OrderByDescending(item => item.Timestamp)
                .Take(50)
                .ToArray();
        }
    }

    public static void Publish(string title, string message, string kind = "info")
    {
        lock (Gate)
        {
            Items.Add(new ShellNotification(Guid.NewGuid(), DateTimeOffset.Now, title, message, kind));
            if (Items.Count > 80)
            {
                Items.RemoveRange(0, Items.Count - 80);
            }
        }

        SceneEngineTrace.Info("notification", $"kind={kind} title={title} message={message}");
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate) Items.Clear();
        Changed?.Invoke();
    }
}
