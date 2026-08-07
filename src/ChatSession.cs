using OpenAI.Chat;

namespace TerminalAiAssistant;

public class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public string? Workspace { get; set; }
    public List<ChatMessage> Messages { get; set; } = [];
    public bool SessionStarted { get; set; }

    /// <summary>Read/write journal enforcing the read-before-edit contract. Session-scoped.</summary>
    public FileStateJournal FileState { get; } = new();

    /// <summary>Undo journal with before-images of every file change. Session-scoped.</summary>
    public UndoJournal Undo { get; } = new();

    public void Reset()
    {
        Messages = [];
        SessionStarted = false;
        FileState.Clear();
        Undo.Clear();
    }
}
