using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class MatchFinderTests
{
    [Fact]
    public void ExactMatch_SingleOccurrence_ReturnsExactStrategyWithCorrectIndexAndLength()
    {
        var result = MatchFinder.FindBestMatch("hello world\nfoo bar\n", "world\nfoo");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.Exact, result.Strategy);
        Assert.Equal(6, result.Index);
        Assert.Equal(9, result.Length);
    }

    [Fact]
    public void ExactMatch_MultipleOccurrences_ReturnsNull()
    {
        var result = MatchFinder.FindBestMatch("aaa\nbbb\nccc\nxxx\naaa\nbbb\nccc\n", "aaa\nbbb\nccc");

        Assert.Null(result);
    }

    [Fact]
    public void ExactMatch_MultiLine_SpanningLines()
    {
        var content = "line one\nline two\nline three\n";
        var result = MatchFinder.FindBestMatch(content, "line two\nline three");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.Exact, result.Strategy);
        Assert.Equal(9, result.Index);
        Assert.Equal(19, result.Length);
    }

    [Fact]
    public void EmptyOldString_ReturnsNull()
    {
        Assert.Null(MatchFinder.FindBestMatch("content", ""));
        Assert.Null(MatchFinder.FindBestMatch("content", null!));
    }

    [Fact]
    public void NotFound_ReturnsNull()
    {
        Assert.Null(MatchFinder.FindBestMatch("hello world", "goodbye world"));
    }

    [Fact]
    public void MatchAtStartOfContent_ReturnsIndexZero()
    {
        var result = MatchFinder.FindBestMatch("target line\nrest of file", "target line");

        Assert.NotNull(result);
        Assert.Equal(0, result.Index);
        Assert.Equal(MatchStrategy.Exact, result.Strategy);
    }

    [Fact]
    public void MatchAtEndOfContent_ReturnsEndPosition()
    {
        var content = "line1\nline2";
        var result = MatchFinder.FindBestMatch(content, "line2");

        Assert.NotNull(result);
        Assert.Equal(6, result.Index);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void WhitespaceTolerance_TrimsLines_ReturnsNormalizedWhitespace()
    {
        var result = MatchFinder.FindBestMatch("  hello  \n  world  \n", "hello\nworld");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.NormalizedWhitespace, result.Strategy);
        Assert.Equal(2, result.Index);
        Assert.Equal(17, result.Length);
    }

    [Fact]
    public void TrailingWhitespaceInOldString_ReturnsNormalizedWhitespace()
    {
        var result = MatchFinder.FindBestMatch("hello\n", "hello ");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.NormalizedWhitespace, result.Strategy);
        Assert.Equal(0, result.Index);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void CrlfVsLf_MatchesViaWhitespaceTier()
    {
        var content = "alpha\r\nbeta\r\ngamma\r\n";
        var result = MatchFinder.FindBestMatch(content, "alpha\nbeta");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.NormalizedWhitespace, result.Strategy);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void CaseDifference_DoesNotMatch()
    {
        Assert.Null(MatchFinder.FindBestMatch("Hello World\n", "hello world"));
    }

    [Fact]
    public void UnicodeCurlyQuotes_MatchWholeLine_ReturnsUnicodeNormalized()
    {
        var content = "use \u201Cdouble\u201D quotes\n";
        var result = MatchFinder.FindBestMatch(content, "use \"double\" quotes\n");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.UnicodeNormalized, result.Strategy);
        Assert.Equal(0, result.Index);
        Assert.Equal(content.Length - 1, result.Length);
    }

    [Fact]
    public void UnicodeSingleQuotes_MapToApostrophe()
    {
        var content = "it\u2019s fine\n";
        var result = MatchFinder.FindBestMatch(content, "it's fine\n");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.UnicodeNormalized, result.Strategy);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void UnicodeDash_EnAndEmDashMapToHyphen()
    {
        var content = "use \u2013 and \u2014\n";
        var result = MatchFinder.FindBestMatch(content, "use - and -\n");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.UnicodeNormalized, result.Strategy);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void UnicodeNbsp_MapsToSpace()
    {
        var content = "a\u00A0b\n";
        var result = MatchFinder.FindBestMatch(content, "a b\n");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.UnicodeNormalized, result.Strategy);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void UnicodeMatch_MidLine_ReportsOriginalOffset()
    {
        var content = "prefix then \u201Chello\u201D\n";
        var result = MatchFinder.FindBestMatch(content, "prefix then \"hello\"\n");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.UnicodeNormalized, result.Strategy);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void StrategyPriority_ExactWinsOverFuzzy()
    {
        var content = "  hello  \n";
        var result = MatchFinder.FindBestMatch(content, "  hello  \n");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.Exact, result.Strategy);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Lcs_OneLineDiffers_ReturnsLineLcsWithConfidence()
    {
        var content = "line1\nline2\nline3 changed\nline4\n";
        var result = MatchFinder.FindBestMatch(content, "line1\nline2\nline3 original\nline4");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.LineLcs, result.Strategy);
        Assert.Equal(0.75, result.Confidence);
        Assert.Equal(0, result.Index);
        Assert.Equal(content.Length - 1, result.Length);
    }

    [Fact]
    public void Lcs_InsertedExtraLine_Tolerated()
    {
        var content = "a\nb\nEXTRA\nc\nd\n";
        var result = MatchFinder.FindBestMatch(content, "a\nb\nc\nd");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.LineLcs, result.Strategy);
        Assert.Equal(1.0, result.Confidence);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Lcs_TwoLinesChanged_StillMatchesWithinThreshold()
    {
        var content = "one\ntwo\nAAA\nfour\nfive\n";
        var result = MatchFinder.FindBestMatch(content, "one\ntwo\nthree\nfour\nfive");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.LineLcs, result.Strategy);
        Assert.Equal(0.8, result.Confidence);
        Assert.Equal(0, result.Index);
    }

    [Fact]
    public void Lcs_BelowMinConfidence_ReturnsNull()
    {
        var content = "c1\nc2\nc3\nc4\nc5\nc6\n";
        var result = MatchFinder.FindBestMatch(content,
            "c1\nx1\nc2\nx2\nc3\nx3\nc4\nx4\nc5\nc6");

        Assert.Null(result);
    }

    [Fact]
    public void Lcs_AmbiguousEqualMatches_ReturnsNull()
    {
        var content = "a\nb\nchanged\nc\nd\na\nb\nchanged2\nc\nd\n";
        var result = MatchFinder.FindBestMatch(content, "a\nb\noriginal\nc\nd");

        Assert.Null(result);
    }

    [Fact]
    public void Lcs_PatternOver512Lines_ReturnsNull()
    {
        var lines = Enumerable.Range(0, 600).Select(i => $"line{i}").ToList();
        var pattern = string.Join('\n', lines.Take(513)) + "\nline512 changed";
        var contentLines = lines.Select((l, i) => i == 512 ? "line512 changed" : l).ToList();
        var content = string.Join('\n', contentLines);

        Assert.Null(MatchFinder.FindBestMatch(content, pattern));
    }

    [Fact]
    public void Lcs_MaxCellsExceeded_ReturnsNull()
    {
        var content = string.Join('\n', Enumerable.Range(0, 14_000).Select(i => $"c{i}"));
        var pattern = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"c{i}")) + "\nZZZ";

        Assert.Null(MatchFinder.FindBestMatch(content, pattern));
    }

    [Fact]
    public void Lcs_ResultIndex_CoversFromFirstToLastMatchedLine()
    {
        var content = "a\nb\na\nc";
        var result = MatchFinder.FindBestMatch(content, "a\nb\nc");

        Assert.NotNull(result);
        Assert.Equal(MatchStrategy.LineLcs, result.Strategy);
        Assert.Equal(1.0, result.Confidence);
        Assert.Equal(0, result.Index);
        Assert.Equal(7, result.Length);
    }

    [Fact]
    public void NoMatch_AfterAllTiers_ReturnsNull()
    {
        var content = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"l{i}"));
        Assert.Null(MatchFinder.FindBestMatch(content, "l99\nl98\nl97\nl96\nl95\nl94"));
    }
}
