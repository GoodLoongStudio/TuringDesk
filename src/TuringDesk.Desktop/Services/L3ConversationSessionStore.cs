using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed class L3ConversationSessionStore
{
    private const int MaxStoredMessages = 32;
    private const int MaxStoredCharacters = 48000;

    private readonly object _gate = new();
    private readonly string _path;

    public L3ConversationSessionStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuringDesk");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "l3-conversation.json");
    }

    public IReadOnlyList<L3ChatMessage> Load(string modelKey)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return Array.Empty<L3ChatMessage>();
                var document = JsonSerializer.Deserialize<L3SessionDocument>(File.ReadAllText(_path));
                if (document is null || !string.Equals(document.ModelKey, modelKey, StringComparison.Ordinal))
                    return Array.Empty<L3ChatMessage>();

                return Trim(document.Messages ?? Array.Empty<L3ChatMessage>()).ToArray();
            }
            catch
            {
                return Array.Empty<L3ChatMessage>();
            }
        }
    }

    public void Save(string modelKey, IReadOnlyList<L3ChatMessage> messages)
    {
        lock (_gate)
        {
            try
            {
                var document = new L3SessionDocument(
                    modelKey,
                    DateTimeOffset.UtcNow,
                    Trim(messages).ToArray());
                File.WriteAllText(
                    _path,
                    JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Conversation persistence must never break live chat.
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch
            {
            }
        }
    }

    private static IEnumerable<L3ChatMessage> Trim(IReadOnlyList<L3ChatMessage> messages)
    {
        var selected = new List<L3ChatMessage>();
        var characters = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var length = message.Content?.Length ?? 0;
            if (selected.Count >= MaxStoredMessages || characters + length > MaxStoredCharacters)
                break;
            selected.Add(message);
            characters += length;
        }

        selected.Reverse();
        return selected;
    }

    private sealed record L3SessionDocument(
        string ModelKey,
        DateTimeOffset UpdatedAt,
        L3ChatMessage[] Messages);
}
