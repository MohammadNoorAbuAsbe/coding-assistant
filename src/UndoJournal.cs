using System.Collections.ObjectModel;

namespace TerminalAiAssistant;

/// <summary>
/// Session-scoped journal of file modifications made by the assistant's
/// file-modifying tools (Write, Edit, ApplyPatch). Each entry holds the
/// file's before-image so the user can roll back a change with /undo.
/// Each <see cref="ChatSession"/> owns one instance. Undo snapshots are
/// intentionally NOT persisted across restarts: the before-image of a file
/// from a previous run could clobber newer on-disk edits.
/// </summary>
public sealed class UndoJournal
{
    private readonly List<UndoEntry> _entries = new();
    private readonly object _gate = new();

    /// <summary>
    /// Records the state of a file immediately before it is written.
    /// </summary>
    /// <param name="fullPath">Validated absolute path of the file being modified.</param>
    /// <param name="beforeContent">The file's current content, or null if it did not exist.</param>
    /// <param name="existedBefore">Whether the file existed before this modification.</param>
    /// <param name="toolName">The tool that is about to modify the file.</param>
    public void Record(string fullPath, string? beforeContent, bool existedBefore, string toolName)
    {
        lock (_gate)
        {
            _entries.Add(new UndoEntry(fullPath, beforeContent, existedBefore, toolName, DateTime.Now));
            int limit = Configuration.GetUndoHistoryLimit();
            if (_entries.Count > limit)
                _entries.RemoveRange(0, _entries.Count - limit);
        }
        AppUi.PublishChanges(this);
    }

    /// <summary>
    /// Pops the most recent entry and restores the file to its before-image:
    /// content is rewritten if the file existed, or the file is deleted if it
    /// was created by the recorded modification. Returns null if the journal
    /// is empty.
    /// </summary>
    public UndoEntry? UndoLast()
    {
        UndoEntry? entry;
        lock (_gate)
        {
            if (_entries.Count == 0)
                return null;
            entry = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
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

    public UndoEntry? Peek()
    {
        lock (_gate)
        {
            return _entries.Count > 0 ? _entries[^1] : null;
        }
    }

    /// <summary>
    /// Reverts the entry at the given index (0 = most recent) and restores the
    /// file to its before-image, mirroring <see cref="UndoLast"/>. Returns null
    /// if the index is out of range.
    /// </summary>
    public UndoEntry? UndoAt(int index)
    {
        UndoEntry? entry;
        lock (_gate)
        {
            if (index < 0 || index >= _entries.Count)
                return null;
            entry = _entries[_entries.Count - 1 - index];
            _entries.RemoveAt(_entries.Count - 1 - index);
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
    public IReadOnlyList<UndoEntry> List()
    {
        lock (_gate)
        {
            return _entries.Count == 0 ? [] : new ReadOnlyCollection<UndoEntry>(_entries.AsEnumerable().Reverse().ToList());
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
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
