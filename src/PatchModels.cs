namespace TerminalAiAssistant;

internal static partial class PatchHandler
{
    private sealed class Hunk
    {
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
        public List<PatchLine> Lines { get; } = [];

        public int SearchBlockCount => Lines.Count(e => e.Type != '+');
        public int ContextCount => Lines.Count(e => e.Type == ' ');
        public int RemovedCount => Lines.Count(e => e.Type == '-');
        public int AddedCount => Lines.Count(e => e.Type == '+');
        public bool LastEntryHasNoNewlineMarker { get; set; }
    }

    private sealed record PatchLine(char Type, string Text);

    private sealed record DiffChange(char Type, string Text, bool MarkerAfter = false);

    private sealed class DiffHunk
    {
        public DiffHunk(int oldStart, int oldCount, int newStart, int newCount, List<DiffChange> entries)
        {
            OldStart = oldStart;
            OldCount = oldCount;
            NewStart = newStart;
            NewCount = newCount;
            Entries = entries;
        }

        public int OldStart { get; }
        public int OldCount { get; }
        public int NewStart { get; }
        public int NewCount { get; }
        public List<DiffChange> Entries { get; }
    }
}
