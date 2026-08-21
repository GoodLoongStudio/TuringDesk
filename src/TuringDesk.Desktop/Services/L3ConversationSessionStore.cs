using System.IO;
using System.Text.Json;

namespace TuringDesk.Desktop.Services;

public sealed class L3ConversationSessionStore
{
    private const int MaxStoredMessages = 32;
    private const int MaxStoredCharacters = 48000;
    private const int MaxStoredSessions = 8;

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
                var sessions = ReadSessionsNoLock();
                if (!sessions.TryGetValue(modelKey, out var session))
                    return Array.Empty<L3ChatMessage>();

                return Trim(session.Messages ?? Array.Empty<L3ChatMessage>()).ToArray();
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
                var sessions = ReadSessionsNoLock();
                sessions[modelKey] = new L3StoredSession(
                    DateTimeOffset.UtcNow,
                    Trim(messages).ToArray());

                var retained = sessions
                    .OrderByDescending(item => item.Value.UpdatedAt)
                    .Take(MaxStoredSessions)
                    .ToDictionary(
                        item => item.Key,
                        item => item.Value,
                        StringComparer.Ordinal);

                WriteSessionsNoLock(retained);
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

    private Dictionary<string, L3StoredSession> ReadSessionsNoLock()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, L3StoredSession>(StringComparer.Ordinal);

        var json = File.ReadAllText(_path);

        try
        {
            var document = JsonSerializer.Deserialize<L3SessionCollectionDocument>(json);
            if (document?.Sessions is not null)
            {
                return new Dictionary<string, L3StoredSession>(
                    document.Sessions,
                    StringComparer.Ordinal);
            }
        }
        catch (JsonException)
        {
            // Fall through to the v1 single-session migration path.
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<L3LegacySessionDocument>(json);
            if (legacy is not null && !string.IsNullOrWhiteSpace(legacy.ModelKey))
            {
                return new Dictionary<string, L3StoredSession>(StringComparer.Ordinal)
                {
                    [legacy.ModelKey] = new(
                        legacy.UpdatedAt,
                        Trim(legacy.Messages ?? Array.Empty<L3ChatMessage>()).ToArray())
                };
            }
        }
        catch (JsonException)
        {
        }

        return new Dictionary<string, L3StoredSession>(StringComparer.Ordinal);
    }

    private void WriteSessionsNoLock(IReadOnlyDictionary<string, L3StoredSession> sessions)
    {
        var document = new L3SessionCollectionDocument(2, sessions);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _path, overwrite: true);
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

    private sealed record L3SessionCollectionDocument(
        int Version,
        IReadOnlyDictionary<string, L3StoredSession> Sessions);

    private sealed record L3StoredSession(
        DateTimeOffset UpdatedAt,
        L3ChatMessage[] Messages);

    private sealed record L3LegacySessionDocument(
        string ModelKey,
        DateTimeOffset UpdatedAt,
        L3ChatMessage[] Messages);
}
