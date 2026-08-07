namespace TerminalAiAssistant;

/// <summary>
/// Resolves the session-scoped journals for the currently executing run.
/// <see cref="ChatOrchestrator.Run"/> sets <see cref="Current"/> for the
/// duration of a run (AsyncLocal flows through all awaits, tool calls and
/// sub-agents), so tool handlers always touch the active session's journals.
/// When no run is active (tests, UI-thread work) the shared fallback
/// instances are used, preserving the previous global behavior.
/// </summary>
internal static class SessionContext
{
    private static readonly AsyncLocal<ChatSession?> CurrentHolder = new();

    public static ChatSession? Current
    {
        get => CurrentHolder.Value;
        set => CurrentHolder.Value = value;
    }

    public static FileStateJournal FileState => Current?.FileState ?? SharedFileState;
    public static UndoJournal Undo => Current?.Undo ?? SharedUndo;

    private static readonly FileStateJournal SharedFileState = new();
    private static readonly UndoJournal SharedUndo = new();
}
