using System.Security.Cryptography;
using System.Text;

namespace TerminalAiAssistant;

/// <summary>
/// Session-scoped journal of which files the assistant has Read or Written and
/// the content hash at that time. Used to enforce a read-before-edit contract:
/// edits on files the session has never read are refused, and edits on files
/// whose on-disk content changed since the last Read/Write are flagged.
/// Each <see cref="ChatSession"/> owns one instance; hash entries can be
/// persisted with the session and restored safely (any on-disk change since
/// the last read is detected by the hash comparison).
/// </summary>
public sealed class FileStateJournal
{
    private readonly Dictionary<string, JournalEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void RecordRead(string fullPath, string content, int startLine, int endLine)
    {
        lock (_gate)
        {
            _entries[fullPath] = new JournalEntry(ComputeHash(content), writtenBySession: false, startLine, endLine);
        }
    }

    public void RecordWrite(string fullPath, string content)
    {
        lock (_gate)
        {
            _entries[fullPath] = new JournalEntry(ComputeHash(content), writtenBySession: true, 0, 0);
        }
    }

    /// <summary>
    /// True if the session has read or written this file at least once.
    /// </summary>
    public bool HasState(string fullPath)
    {
        lock (_gate)
        {
            return _entries.ContainsKey(fullPath);
        }
    }

    /// <summary>
    /// True if the file's current on-disk content differs from what the session
    /// last read or wrote. False when the session has no state for the file.
    /// </summary>
    public bool IsStale(string fullPath, string currentContent)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(fullPath, out var entry)
                && entry.Hash != ComputeHash(currentContent);
        }
    }

    /// <summary>
    /// The line range (1-based, inclusive) that the session last returned for
    /// this file via the Read tool, or false when the file was never read (or
    /// was only written, never read back).
    /// </summary>
    public bool TryGetReadCoverage(string fullPath, out int startLine, out int endLine)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(fullPath, out var entry)
                && !entry.WrittenBySession
                && entry.LastReadStart > 0)
            {
                startLine = entry.LastReadStart;
                endLine = entry.LastReadEnd;
                return true;
            }
        }
        startLine = 0;
        endLine = 0;
        return false;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    // ── Persistence (safe: staleness is re-detected by hash comparison) ──

    public List<StoredFileState> ToStored()
    {
        lock (_gate)
        {
            return _entries
                .Select(kv => new StoredFileState
                {
                    Path = kv.Key,
                    Hash = kv.Value.Hash,
                    WrittenBySession = kv.Value.WrittenBySession,
                    LastReadStart = kv.Value.LastReadStart,
                    LastReadEnd = kv.Value.LastReadEnd
                })
                .ToList();
        }
    }

    public void Restore(IEnumerable<StoredFileState> stored)
    {
        lock (_gate)
        {
            foreach (var s in stored)
            {
                if (!string.IsNullOrEmpty(s.Path) && !string.IsNullOrEmpty(s.Hash))
                {
                    _entries[s.Path] = new JournalEntry(s.Hash, s.WrittenBySession, s.LastReadStart, s.LastReadEnd);
                }
            }
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
    public JournalEntry(string hash, bool writtenBySession, int lastReadStart, int lastReadEnd)
    {
        Hash = hash;
        WrittenBySession = writtenBySession;
        LastReadStart = lastReadStart;
        LastReadEnd = lastReadEnd;
    }

    public string Hash { get; }
    public bool WrittenBySession { get; }
    public int LastReadStart { get; }
    public int LastReadEnd { get; }
}
