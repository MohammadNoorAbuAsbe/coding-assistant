using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace TerminalAiAssistant;

/// <summary>
/// Persists sessions (conversation context + metadata) as JSON files under
/// %LOCALAPPDATA%\CodingAssistant\sessions so conversations survive restarts.
/// Serializes the raw ChatMessage list — including tool calls, tool results
/// and compaction summaries — so a restored session keeps full context.
/// </summary>
internal static class SessionStore
{
    public const int MaxHistorySessions = 100;

    private static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodingAssistant", "sessions");

    private static string? _dirOverride;

    /// <summary>Storage directory. Overridable for tests.</summary>
    public static string StorageDir
    {
        get => _dirOverride ?? DefaultDir;
        set => _dirOverride = value;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Persistence ─────────────────────────────────────────────────

    public static void Save(ChatSession session)
    {
        try
        {
            session.Title = DeriveTitle(session);
            var stored = ToStored(session);
            session.UpdatedAt = stored.UpdatedAt = DateTimeOffset.Now;
            Directory.CreateDirectory(StorageDir);
            File.WriteAllText(PathFor(stored.Id), JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch
        {
            // Persistence is best-effort; never crash the session loop.
        }
    }

    public static StoredSession? Load(string id)
    {
        try
        {
            string path = PathFor(id);
            if (!File.Exists(path))
                return null;
            var stored = JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(path), JsonOptions);
            return stored is { Id.Length: > 0 } ? stored : null;
        }
        catch
        {
            return null; // Corrupt files are skipped.
        }
    }

    /// <summary>All persisted sessions, most recently updated first.</summary>
    public static List<StoredSession> List(int max = MaxHistorySessions)
    {
        try
        {
            if (!Directory.Exists(StorageDir))
                return [];
            return Directory.EnumerateFiles(StorageDir, "*.json")
                .Select(SafeRead)
                .Where(s => s != null)
                .OrderByDescending(s => s!.UpdatedAt)
                .Take(max)
                .Select(s => s!)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static bool Delete(string id)
    {
        try
        {
            string path = PathFor(id);
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string PathFor(string id) => Path.Combine(StorageDir, id + ".json");

    private static StoredSession? SafeRead(string file)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredSession>(File.ReadAllText(file), JsonOptions);
            return stored is { Id.Length: > 0 } ? stored : null;
        }
        catch
        {
            return null;
        }
    }

    // ── ChatSession ⇄ StoredSession ─────────────────────────────────

    public static StoredSession ToStored(ChatSession session) => new()
    {
        Id = session.Id,
        Title = DeriveTitle(session),
        CreatedAt = session.CreatedAt,
        UpdatedAt = session.UpdatedAt,
        Workspace = session.Workspace,
        SessionStarted = session.SessionStarted,
        Messages = session.Messages.Select(ToStoredMessage).Where(m => m != null).Select(m => m!).ToList(),
        FileState = session.FileState.ToStored()
    };

    public static ChatSession ToSession(StoredSession stored)
    {
        var session = new ChatSession
        {
            Id = stored.Id,
            Title = stored.Title,
            CreatedAt = stored.CreatedAt,
            UpdatedAt = stored.UpdatedAt,
            Workspace = stored.Workspace,
            SessionStarted = stored.SessionStarted,
            Messages = stored.Messages.Select(ToChatMessage).Where(m => m != null).Select(m => m!).ToList()
        };
        session.FileState.Restore(stored.FileState);
        return session;
    }

    /// <summary>
    /// Display title: the first user message, collapsed to a single line
    /// (64 chars max). Falls back to the existing title or "Session".
    /// </summary>
    public static string DeriveTitle(ChatSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Title))
            return session.Title;

        foreach (var msg in session.Messages)
        {
            if (msg is not UserChatMessage user)
                continue;
            string text = ContextManager.ExtractText(user.Content).Trim();
            if (text.Length == 0)
                continue;
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            while (text.Contains("  ", StringComparison.Ordinal))
                text = text.Replace("  ", " ");
            return text.Length <= 64 ? text : text[..64].TrimEnd() + "…";
        }
        return "Session";
    }

    private static StoredMessage? ToStoredMessage(ChatMessage msg) => msg switch
    {
        SystemChatMessage s => new StoredMessage { Role = "system", Text = ContextManager.ExtractText(s.Content) },
        UserChatMessage u => new StoredMessage { Role = "user", Text = ContextManager.ExtractText(u.Content) },
        AssistantChatMessage a => new StoredMessage
        {
            Role = "assistant",
            Text = a.Content is { } ac ? ContextManager.ExtractText(ac) : null,
            ToolCalls = a.ToolCalls?
                .Select(t => new StoredToolCall
                {
                    Id = t.Id,
                    Name = t.FunctionName,
                    Arguments = t.FunctionArguments?.ToString()
                })
                .Where(t => !string.IsNullOrEmpty(t.Id))
                .ToList()
        },
        ToolChatMessage t => new StoredMessage
        {
            Role = "tool",
            Text = t.Content is { } tc ? ContextManager.ExtractText(tc) : null,
            ToolCallId = t.ToolCallId
        },
        _ => null
    };

    private static ChatMessage? ToChatMessage(StoredMessage stored) => stored.Role switch
    {
        "system" => new SystemChatMessage(stored.Text ?? ""),
        "user" => new UserChatMessage(stored.Text ?? ""),
        "tool" => new ToolChatMessage(stored.ToolCallId ?? "", stored.Text ?? ""),
        "assistant" => stored.ToolCalls is { Count: > 0 }
            ? new AssistantChatMessage(stored.ToolCalls.Select(t =>
                ChatToolCall.CreateFunctionToolCall(t.Id, t.Name, BinaryData.FromString(t.Arguments ?? ""))))
            : new AssistantChatMessage(stored.Text ?? ""),
        _ => null
    };
}

public sealed class StoredSession
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? Workspace { get; set; }
    public bool SessionStarted { get; set; }
    public List<StoredMessage> Messages { get; set; } = [];
    public List<StoredFileState> FileState { get; set; } = [];
}

public sealed class StoredMessage
{
    public string Role { get; set; } = "";
    public string? Text { get; set; }
    public string? ToolCallId { get; set; }
    public List<StoredToolCall>? ToolCalls { get; set; }
}

public sealed class StoredToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Arguments { get; set; }
}

/// <summary>
/// Persisted entry of the session's read/write journal (file path + content
/// hash). Safe to restore because any on-disk change since the last read is
/// detected by the hash comparison.
/// </summary>
public sealed class StoredFileState
{
    public string Path { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool WrittenBySession { get; set; }
    public int LastReadStart { get; set; }
    public int LastReadEnd { get; set; }
}
