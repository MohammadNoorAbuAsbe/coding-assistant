using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class GlobHelperTests
{
    [Fact]
    public void FindFiles_RecursivePattern_FindsNestedFiles()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "x");
        ws.WriteFile("b.txt", "x");
        ws.WriteFile("sub/c.txt", "x");

        string result = GlobHelper.FindFiles("**/*.txt", ws.Root);

        Assert.Contains(Path.Combine(ws.Root, "a.txt"), result);
        Assert.Contains(Path.Combine(ws.Root, "b.txt"), result);
        Assert.Contains(Path.Combine(ws.Root, "sub", "c.txt"), result);
    }

    [Fact]
    public void FindFiles_TopLevelPattern_ExcludesSubdirectories()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "x");
        ws.WriteFile("b.txt", "x");
        ws.WriteFile("sub/c.txt", "x");

        string result = GlobHelper.FindFiles("*.txt", ws.Root);

        Assert.Contains(Path.Combine(ws.Root, "a.txt"), result);
        Assert.DoesNotContain(Path.Combine(ws.Root, "sub", "c.txt"), result);
    }

    [Fact]
    public void FindFiles_AlternationPattern_NotSupported()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.md", "x");
        ws.WriteFile("b.txt", "x");

        string result = GlobHelper.FindFiles("*.{md,txt}", ws.Root);

        Assert.Contains("No files matching pattern '*.{md,txt}' found in", result);
    }

    [Fact]
    public void FindFiles_SingleCharWildcard_NotSupported()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a1.txt", "x");

        string result = GlobHelper.FindFiles("a?.txt", ws.Root);

        Assert.Contains("No files matching pattern 'a?.txt' found in", result);
    }

    [Fact]
    public void FindFiles_SubdirectoryPattern_FindsNested()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("sub/one.md", "x");
        ws.WriteFile("sub/two.md", "x");
        ws.WriteFile("other/three.md", "x");

        string result = GlobHelper.FindFiles("sub/*.md", ws.Root);

        Assert.Contains(Path.Combine(ws.Root, "sub", "one.md"), result);
        Assert.DoesNotContain(Path.Combine(ws.Root, "other", "three.md"), result);
    }

    [Fact]
    public void FindFiles_NoMatches_ReturnsMessage()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "x");

        string result = GlobHelper.FindFiles("**/*.pdf", ws.Root);

        Assert.Contains("No files matching pattern '**/*.pdf' found in", result);
    }

    [Fact]
    public void FindFiles_MissingDirectory_ReturnsError()
    {
        using var ws = new TempWorkspace();
        string missing = Path.Combine(ws.Root, "nope");

        string result = GlobHelper.FindFiles("**/*.txt", missing);

        Assert.Contains("does not exist", result);
    }

    [Fact]
    public void FindFiles_Over100Matches_CappedWithMessage()
    {
        using var ws = new TempWorkspace();
        for (int i = 0; i < 120; i++)
        {
            ws.WriteFile($"file{i:D3}.txt", "x");
        }

        string result = GlobHelper.FindFiles("*.txt", ws.Root);

        Assert.Contains("[showing 100 of 120 matches", result);
    }

    [Fact]
    public void FindFiles_Exactly100Matches_NoCapMessage()
    {
        using var ws = new TempWorkspace();
        for (int i = 0; i < 100; i++)
        {
            ws.WriteFile($"file{i:D3}.txt", "x");
        }

        string result = GlobHelper.FindFiles("*.txt", ws.Root);

        Assert.DoesNotContain("showing", result);
    }
}
