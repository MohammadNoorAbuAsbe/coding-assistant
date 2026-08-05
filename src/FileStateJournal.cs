using System.Security.Cryptography;
using System.Text;

namespace TerminalAiAssistant;

/// <summary>
/// Session-scoped journal of which files the assistant has Read or Written and
/// the content hash at that time. Used to enforce a read-before-edit contract:
/// edits on files the session has never read are refused, and edits on files
/// whose on-disk content changed since the last Read/Write are flagged.
/// Cleared on session reset (/new) alongside UndoJournal.
/// </summary>
public static class FileStateJournal
{
    private static readonly Dictionary<string, JournalEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static void RecordRead(string fullPath, string content)
    {
        lock (Gate)
        {
            Entries[fullPath] = new JournalEntry(ComputeHash(content), writtenBySession: false);
        }
    }

    public static void RecordWrite(string fullPath, string content)
    {
        lock (Gate)
        {
            Entries[fullPath] = new JournalEntry(ComputeHash(content), writtenBySession: true);
        }
    }

    /// <summary>
    /// True if the session has read or written this file at least once.
    /// </summary>
    public static bool HasState(string fullPath)
    {
        lock (Gate)
        {
            return Entries.ContainsKey(fullPath);
        }
    }

    /// <summary>
    /// True if the file's current on-disk content differs from what the session
    /// last read or wrote. False when the session has no state for the file.
    /// </summary>
    public static bool IsStale(string fullPath, string currentContent)
    {
        lock (Gate)
        {
            return Entries.TryGetValue(fullPath, out var entry)
                && entry.Hash != ComputeHash(currentContent);
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }

    private static string ComputeHash(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}

internal sealed class JournalEntry
{
    public JournalEntry(string hash, bool writtenBySession)
    {
        Hash = hash;
        WrittenBySession = writtenBySession;
    }

    public string Hash { get; }
    public bool WrittenBySession { get; }
}
