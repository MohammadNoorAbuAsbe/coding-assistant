using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class PatchEndToEndTests
{
    public PatchEndToEndTests()
    {
        Configuration.LoadProviderConfigs();
    }

    private static string ToolText(ToolChatMessage message)
    {
        Assert.NotNull(message.Content);
        return string.Join("", message.Content!.Select(p => p.Text ?? ""));
    }

    private static async Task<ToolChatMessage?> RunApplyPatchAsync(TempWorkspace ws, string filePath, string patch)
    {
        var toolCall = ToolCallFactory.Create(ToolHandler.ApplyPatchFunctionName,
            System.Text.Json.JsonSerializer.Serialize(new { file_path = filePath, patch }));
        return await ResponseHandler.ProcessSingleToolCallAsync(toolCall);
    }

    private static async Task<ToolChatMessage?> RunDiffAsync(TempWorkspace ws, string filePath, string newContent)
    {
        var toolCall = ToolCallFactory.Create(ToolHandler.DiffFunctionName,
            System.Text.Json.JsonSerializer.Serialize(new { file_path = filePath, new_content = newContent }));
        return await ResponseHandler.ProcessSingleToolCallAsync(toolCall);
    }

    [Fact]
    public async Task ApplyPatch_SingleHunk_ReplacesLines()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "one\ntwo\nthree\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three");

        string text = ToolText(message!);
        Assert.Contains("Successfully applied 1 hunk(s) to file.txt", text);
        Assert.Contains("matched exactly", text);
        Assert.Equal("one\nTWO\nthree\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_NeverReadFile_Refuses()
    {
        using var ws = new TempWorkspace();
        SessionContext.FileState.Clear();
        System.IO.File.WriteAllText(System.IO.Path.Combine(ws.Root, "file.txt"), "one\ntwo\nthree\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three");

        string text = ToolText(message!);
        Assert.Contains("was not read in this session", text);
        Assert.Equal("one\ntwo\nthree\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_StaleFile_WarnsAndApplies()
    {
        using var ws = new TempWorkspace();
        SessionContext.FileState.Clear();
        ws.WriteFile("file.txt", "one\ntwo\nthree\n");
        System.IO.File.WriteAllText(System.IO.Path.Combine(ws.Root, "file.txt"), "one\nTWO\nthree\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@ -1,3 +1,3 @@\n one\n-TWO\n+two\n three");

        string text = ToolText(message!);
        Assert.Contains("changed on disk since the session last read or wrote it", text);
        Assert.Contains("Successfully applied", text);
        Assert.Equal("one\ntwo\nthree\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_MultiHunk_AppliesAllInOneCall()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt",
            "aaa\nbbb\nccc\nddd\neee\nfff\nggg\nhhh\niii\njjj\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@ -1,3 +1,3 @@\n aaa\n-bbb\n+BBB\n ccc\n" +
            "@@ -8,3 +8,3 @@\n hhh\n-iii\n+III\n jjj");

        string text = ToolText(message!);
        Assert.Contains("Successfully applied 2 hunk(s)", text);
        Assert.Contains("hunk 1:", text);
        Assert.Contains("hunk 2:", text);
        Assert.Equal("aaa\nBBB\nccc\nddd\neee\nfff\nggg\nhhh\nIII\njjj\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_BareHeader_Accepted()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "alpha\nbeta\ngamma\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@\n alpha\n-beta\n+BETA\n gamma");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("alpha\nBETA\ngamma\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_CodeFences_Ignored()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "```diff\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n```");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("a\nB\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_FileHeaders_Ignored()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "--- a/file.txt\n+++ b/file.txt\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("a\nB\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_MalformedHunkHeader_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -abc +def @@\n a\n-b\n+B\n c");

        string text = ToolText(message!);
        Assert.Contains("malformed hunk header", text);
        Assert.Equal("a\nb\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_NoHunks_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "just some text");

        Assert.Contains("no hunks found in the patch", ToolText(message!));
        Assert.Equal("a\nb\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_UnexpectedLineInsideHunk_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,3 +1,3 @@\n a\nbare line without prefix\n c");

        Assert.Contains("unexpected line", ToolText(message!));
        Assert.Equal("a\nb\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_ContextOnlyNoOp_ReportsNoChanges()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,3 +1,3 @@\n a\n b\n c");

        string text = ToolText(message!);
        Assert.Contains("Successfully applied the patch to file.txt", text);
        Assert.Contains("context-only no-op", text);
        Assert.Equal("a\nb\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_CreateNewFile_AllAdditions()
    {
        using var ws = new TempWorkspace();

        var message = await RunApplyPatchAsync(ws, "new.txt",
            "@@ -0,0 +1,2 @@\n+hello\n+world\n");

        string text = ToolText(message!);
        Assert.Contains("Created new file new.txt", text);
        Assert.Contains("+2 lines", text);
        Assert.Equal("hello\nworld\n", ws.ReadFile("new.txt"));
    }

    [Fact]
    public async Task ApplyPatch_CreateNewFile_RejectedWithRemovedLines()
    {
        using var ws = new TempWorkspace();

        var message = await RunApplyPatchAsync(ws, "new.txt",
            "@@ -1,2 +1,2 @@\n-old\n+new\n");

        Assert.Contains("must contain only '+' (added) lines", ToolText(message!));
        Assert.False(File.Exists(Path.Combine(ws.Root, "new.txt")));
    }

    [Fact]
    public async Task ApplyPatch_CreateNewFile_RejectedWithContextLines()
    {
        using var ws = new TempWorkspace();

        var message = await RunApplyPatchAsync(ws, "new.txt",
            "@@ -0,0 +1,2 @@\n ctx\n+new\n");

        Assert.Contains("must contain only '+' (added) lines", ToolText(message!));
        Assert.False(File.Exists(Path.Combine(ws.Root, "new.txt")));
    }

    [Fact]
    public async Task ApplyPatch_MissingFile_ErrorWhenMatchingHunk()
    {
        using var ws = new TempWorkspace();

        var message = await RunApplyPatchAsync(ws, "missing.txt", "@@ -1,2 +1,2 @@\n a\n-b\n+B");

        string text = ToolText(message!);
        Assert.Contains("file not found 'missing.txt'", text);
        Assert.False(File.Exists(Path.Combine(ws.Root, "missing.txt")));
    }

    [Fact]
    public async Task ApplyPatch_AmbiguousContext_ReturnsErrorWithLocations()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "AAA\nfoo\nbar\nxxx\nfoo\nbar\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@\n foo\n-bar\n+baz\n");

        string text = ToolText(message!);
        Assert.Contains("matched at multiple locations", text);
        Assert.Equal("AAA\nfoo\nbar\nxxx\nfoo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_AmbiguityResolvedByDeclaredPosition_FirstBlock()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "AAA\nfoo\nbar\nxxx\nfoo\nbar\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -2,2 +2,2 @@\n foo\n-bar\n+baz\n");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("AAA\nfoo\nbaz\nxxx\nfoo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_AmbiguityResolvedByDeclaredPosition_SecondBlock()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "AAA\nfoo\nbar\nxxx\nfoo\nbar\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -5,2 +5,2 @@\n foo\n-bar\n+baz\n");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("AAA\nfoo\nbar\nxxx\nfoo\nbaz\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_FuzzyWhitespaceContext_ReportsWhitespaceStrategy()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\n  padded  \nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,3 +1,3 @@\n a\n-padded\n+P\n c");

        string text = ToolText(message!);
        Assert.Contains("Successfully applied 1 hunk(s)", text);
        Assert.Contains("fuzzily (whitespace)", text);
        Assert.Equal("a\nP\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_UnicodeContext_ReportsUnicodeStrategy()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\n\u201Cquoted\u201D\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,3 +1,3 @@\n a\n-\"quoted\"\n+Q\n c");

        string text = ToolText(message!);
        Assert.Contains("Successfully applied 1 hunk(s)", text);
        Assert.Contains("fuzzily (unicode)", text);
        Assert.Equal("a\nQ\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_LcsContext_ReportsConfidence()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nsimilar but different\nc\nd\ne\nf\n");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@ -1,5 +1,4 @@\n a\n-similar but different\n c\n D\n e");

        string text = ToolText(message!);
        Assert.Contains("Successfully applied 1 hunk(s)", text);
        Assert.Contains("LCS comparison (confidence", text);
        Assert.Equal("a\nc\nd\ne\nf\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_NoContextNoPosition_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@\n+b\n");

        Assert.Contains("cannot determine where to insert these lines", ToolText(message!));
    }

    [Fact]
    public async Task ApplyPatch_InsertionOnlyHunk_WithDeclaredPosition()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -2,0 +2,1 @@\n+b\n");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("a\nb\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_NoNewlineAtEofMarker_RemovesTrailingNewline()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb");

        var message = await RunApplyPatchAsync(ws, "file.txt",
            "@@ -1,2 +1,2 @@\n a\n-b\n+c\n\\ No newline at end of file");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("a\nc", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_WithoutMarker_AddsTrailingNewline()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,2 +1,2 @@\n a\n-b\n+c");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("a\nc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_CrlfFile_PreservesCrlfEol()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\r\nb\r\nc\r\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,3 +1,3 @@\n a\n-b\n+B\n c");

        Assert.Contains("Successfully applied 1 hunk(s)", ToolText(message!));
        Assert.Equal("a\r\nB\r\nc\r\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_ContextLineNotFound_ReturnsErrorWithLocation()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "aaa\nbbb\nccc\n");

        var message = await RunApplyPatchAsync(ws, "file.txt", "@@ -1,3 +1,3 @@\n zzz\n-bbb\n+B\n ccc");

        string text = ToolText(message!);
        Assert.Contains("could not apply hunk 1", text);
        Assert.Contains("declared around line 1", text);
        Assert.Equal("aaa\nbbb\nccc\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_InvalidJsonArguments_ReturnsFormatError()
    {
        using var ws = new TempWorkspace();

        var toolCall = ToolCallFactory.Create(ToolHandler.ApplyPatchFunctionName, "not valid json");
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("called with invalid arguments", ToolText(message!));
    }

    [Fact]
    public async Task ApplyPatch_MissingParameters_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var toolCall = ToolCallFactory.Create(ToolHandler.ApplyPatchFunctionName,
            System.Text.Json.JsonSerializer.Serialize(new { file_path = "file.txt" }));
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("called with invalid arguments", ToolText(message!));
    }

    [Fact]
    public async Task ApplyPatch_PathOutsideWorkspace_ReturnsError()
    {
        using var ws = new TempWorkspace();

        string outside = Path.Combine(Path.GetTempPath(), "taa-e2e-outside", "file.txt");
        var toolCall = ToolCallFactory.Create(ToolHandler.ApplyPatchFunctionName,
            System.Text.Json.JsonSerializer.Serialize(new { file_path = outside, patch = "@@ -1,2 +1,2 @@\n a\n-b\n+B" }));
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Access denied", ToolText(message!));
    }

    [Fact]
    public async Task Diff_ExistingFile_ReturnsUnifiedDiff()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "one\ntwo\nthree\n");

        var message = await RunDiffAsync(ws, "file.txt", "one\nTWO\nthree\n");

        string text = ToolText(message!);
        Assert.Contains("--- file.txt", text);
        Assert.Contains("+++ file.txt", text);
        Assert.Contains("@@", text);
        Assert.Contains("-two", text);
        Assert.Contains("+TWO", text);
        Assert.Equal("one\ntwo\nthree\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Diff_IdenticalContent_ReportsNoDifferences()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "same\ncontent\n");

        var message = await RunDiffAsync(ws, "file.txt", "same\ncontent\n");

        Assert.Contains("No differences between 'file.txt' and new_content", ToolText(message!));
    }

    [Fact]
    public async Task Diff_MissingFile_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunDiffAsync(ws, "missing.txt", "content");

        Assert.Contains("Error: file not found 'missing.txt'", ToolText(message!));
    }

    [Fact]
    public async Task RoundTrip_DiffThenApply_ProducesExactContent()
    {
        var cases = new (string Old, string New)[]
        {
            ("one\ntwo\nthree\n", "one\nTWO\nthree\n"),
            ("one\ntwo\nthree\n", "one\n"),
            ("one\n", "one\ntwo\nthree\n"),
            ("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n", "a\nX\nc\nd\ne\nY\ng\nh\nZ\nj\n"),
            ("hello\nworld", "hello\nbrave\nnew\nworld"),
            ("caf\u00E9\nt\u00E9st\n", "caf\u00E9 cr\u00E8me\nt\u00E9st\n"),
            ("\u201Cquoted\u201D\nline\n", "\"quoted\"\nline changed\n"),
            ("a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n")
        };

        foreach (var (oldText, newText) in cases)
        {
            using var ws = new TempWorkspace();
            string path = ws.WriteFile("rt.txt", oldText);

            string diff = PatchHandler.GenerateUnifiedDiff(oldText, newText, "rt.txt");
            var message = await RunApplyPatchAsync(ws, "rt.txt", diff);

            Assert.Contains("Successfully applied", ToolText(message!));
            Assert.Equal(newText, ws.ReadFile("rt.txt"));
        }
    }
}
