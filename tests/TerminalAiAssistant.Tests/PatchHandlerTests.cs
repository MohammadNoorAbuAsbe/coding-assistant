using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class PatchHandlerTests
{
    [Fact]
    public void GenerateUnifiedDiff_SingleLineChange_ProducesHeadersAndHunk()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("one\ntwo\nthree", "one\nTWO\nthree", "src/Foo.cs");

        Assert.StartsWith("--- src/Foo.cs", diff);
        Assert.Contains("+++ src/Foo.cs", diff);
        Assert.Contains("@@ -1,3 +1,3 @@", diff);
        Assert.Contains(" one", diff);
        Assert.Contains("-two", diff);
        Assert.Contains("+TWO", diff);
        Assert.Contains(" three", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_NoChanges_ReturnsEmpty()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("same\ncontent", "same\ncontent", "x.cs");

        Assert.Equal("", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_FileTooLarge_Throws()
    {
        var oldText = string.Join('\n', Enumerable.Range(0, 2100).Select(i => $"line{i}"));
        var newText = oldText + "\n" + string.Join('\n', Enumerable.Range(0, 2100).Select(i => $"line{i}"));

        Assert.Throws<InvalidOperationException>(() => PatchHandler.GenerateUnifiedDiff(oldText, newText, "big.cs"));
    }

    [Fact]
    public void GenerateUnifiedDiff_AppendAtEndOfFile_HunkHeader()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("a\nb", "a\nb\nc", "f.txt");

        Assert.Contains("@@ -1,2 +1,3 @@", diff);
        Assert.Contains("+c", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_PrependAtStartOfFile_HunkHeader()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("b\nc", "a\nb\nc", "f.txt");

        Assert.Contains("@@ -1,2 +1,3 @@", diff);
        Assert.Contains("+a", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_RemoveLine_HunkHeader()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("a\nb\nc", "a\nc", "f.txt");

        Assert.Contains("@@ -1,3 +1,2 @@", diff);
        Assert.Contains("-b", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_RemoveAtStart_HunkHeader()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("a\nb\nc", "b\nc", "f.txt");

        Assert.Contains("@@ -1,3 +1,2 @@", diff);
        Assert.Contains("-a", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_TwoDistantChanges_ProducesTwoHunks()
    {
        var oldLines = Enumerable.Range(1, 10).Select(i => $"line{i}").ToList();
        var newLines = oldLines.ToList();
        newLines[0] = "line1 changed";
        newLines[^1] = "line10 changed";
        string oldText = string.Join('\n', oldLines);
        string newText = string.Join('\n', newLines);

        string diff = PatchHandler.GenerateUnifiedDiff(oldText, newText, "f.txt");

        var hunkHeaders = System.Text.RegularExpressions.Regex.Matches(diff, "@@ -[^@]+@@").Count;
        Assert.Equal(2, hunkHeaders);
        Assert.Contains("+line1 changed", diff);
        Assert.Contains("+line10 changed", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_ChangesWithin3Lines_MergeIntoSingleHunk()
    {
        var oldLines = Enumerable.Range(1, 8).Select(i => $"line{i}").ToList();
        var newLines = oldLines.ToList();
        newLines[3] = "line4 changed";
        newLines[5] = "line6 changed";
        string oldText = string.Join('\n', oldLines);
        string newText = string.Join('\n', newLines);

        string diff = PatchHandler.GenerateUnifiedDiff(oldText, newText, "f.txt");

        var hunkHeaders = System.Text.RegularExpressions.Regex.Matches(diff, "@@ -[^@]+@@").Count;
        Assert.Equal(1, hunkHeaders);
        Assert.Contains("+line4 changed", diff);
        Assert.Contains("+line6 changed", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_UnicodeContent_Preserved()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("caf\u00E9\n", "caf\u00E9 cr\u00E8me\n", "f.txt");

        Assert.Contains("+caf\u00E9 cr\u00E8me", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_EmptyOldFile_AddsLines()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("", "hello\nworld\n", "f.txt");

        Assert.Contains("@@ -0,0 +1,", diff);
        Assert.Contains("+hello", diff);
        Assert.Contains("+world", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_EmptyNewFile_RemovesAllLines()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("hello\nworld\n", "", "f.txt");

        Assert.Contains("@@ -1,", diff);
        Assert.Contains("+0,0 @@", diff);
        Assert.Contains("-hello", diff);
        Assert.Contains("-world", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_CrlfFile_TrimsCarriageReturnsInOutput()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("a\r\nb\r\nc\r\n", "a\r\nb2\r\nc\r\n", "f.txt");

        Assert.Contains("-b", diff);
        Assert.Contains("+b2", diff);
        Assert.DoesNotContain("b\r\r", diff);
        Assert.DoesNotContain("b2\r\r", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_NoTrailingNewline_EmitsMarker()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("a\nb\nc\nd\n", "a\nb\nX\nc\nd", "f.txt");

        Assert.Contains(" d", diff);
        Assert.Contains("\\ No newline at end of file", diff);
    }

    [Fact]
    public void GenerateUnifiedDiff_NoTrailingNewline_AddedLine_EmitsMarker()
    {
        string diff = PatchHandler.GenerateUnifiedDiff("a\n", "a\nb", "f.txt");

        Assert.Contains("+b", diff);
        Assert.Contains("\\ No newline at end of file", diff);
    }
}
