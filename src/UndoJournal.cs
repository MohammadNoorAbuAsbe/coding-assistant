using System.Collections.ObjectModel;

namespace TerminalAiAssistant;

/// <summary>
/// In-memory, session-scoped journal of file modifications made by the
/// assistant's file-modifying tools (Write, Edit, ApplyPatch). Each entry
/// holds the file's before-image so the user can roll back a change with
/// /undo. Cleared on session reset (/new).
/// </summary>
public static class UndoJournal
{
    private static readonly List<UndoEntry> Entries = new();
    private static readonly object Gate = new();

    /// <summary>
    /// Records the state of a file immediately before it is written.
    /// </summary>
    /// <param name="fullPath">Validated absolute path of the file being modified.</param>
    /// <param name="beforeContent">The file's current content, or null if it did not exist.</param>
    /// <param name="existedBefore">Whether the file existed before this modification.</param>
    /// <param name="toolName">The tool that is about to modify the file.</param>
    public static void Record(string fullPath, string? beforeContent, bool existedBefore, string toolName)
    {
        lock (Gate)
        {
            Entries.Add(new UndoEntry(fullPath, beforeContent, existedBefore, toolName, DateTime.Now));
            int limit = Configuration.GetUndoHistoryLimit();
            if (Entries.Count > limit)
                Entries.RemoveRange(0, Entries.Count - limit);
        }
        AppUi.PublishChanges();
    }

    /// <summary>
    /// Pops the most recent entry and restores the file to its before-image:
    /// content is rewritten if the file existed, or the file is deleted if it
    /// was created by the recorded modification. Returns null if the journal
    /// is empty.
    /// </summary>
    public static UndoEntry? UndoLast()
    {
        UndoEntry? entry;
        lock (Gate)
        {
            if (Entries.Count == 0)
                return null;
            entry = Entries[^1];
            Entries.RemoveAt(Entries.Count - 1);
        }

        if (entry.ExistedBefore)
        {
            File.WriteAllText(entry.FullPath, entry.BeforeContent ?? "");
        }
        else if (File.Exists(entry.FullPath))
        {
            File.Delete(entry.FullPath);
        }

        return entry;
    }

    public static UndoEntry? Peek()
    {
        lock (Gate)
        {
            return Entries.Count > 0 ? Entries[^1] : null;
        }
    }

    /// <summary>
    /// Reverts the entry at the given index (0 = most recent) and restores the
    /// file to its before-image, mirroring <see cref="UndoLast"/>. Returns null
    /// if the index is out of range.
    /// </summary>
    public static UndoEntry? UndoAt(int index)
    {
        UndoEntry? entry;
        lock (Gate)
        {
            if (index < 0 || index >= Entries.Count)
                return null;
            entry = Entries[Entries.Count - 1 - index];
            Entries.RemoveAt(Entries.Count - 1 - index);
        }

        if (entry.ExistedBefore)
        {
            File.WriteAllText(entry.FullPath, entry.BeforeContent ?? "");
        }
        else if (File.Exists(entry.FullPath))
        {
            File.Delete(entry.FullPath);
        }

        return entry;
    }

    /// <summary>
    /// Returns all entries, most recent first.
    /// </summary>
    public static IReadOnlyList<UndoEntry> List()
    {
        lock (Gate)
        {
            return Entries.Count == 0 ? [] : new ReadOnlyCollection<UndoEntry>(Entries.AsEnumerable().Reverse().ToList());
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}

public sealed class UndoEntry
{
    public UndoEntry(string fullPath, string? beforeContent, bool existedBefore, string toolName, DateTime timestamp)
    {
        FullPath = fullPath;
        BeforeContent = beforeContent;
        ExistedBefore = existedBefore;
        ToolName = toolName;
        Timestamp = timestamp;
    }

    public string FullPath { get; }
    public string? BeforeContent { get; }
    public bool ExistedBefore { get; }
    public string ToolName { get; }
    public DateTime Timestamp { get; }
}
