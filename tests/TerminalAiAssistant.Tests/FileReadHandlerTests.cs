using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class FileReadHandlerTests : IDisposable
{
    private readonly TempWorkspace _ws;

    public FileReadHandlerTests()
    {
        _ws = new TempWorkspace();
        Configuration.LoadProviderConfigs();
        FileStateJournal.Clear();
    }

    public void Dispose()
    {
        FileStateJournal.Clear();
        _ws.Dispose();
    }

    [Fact]
    public void ProcessReadFileCall_FirstRead_ReturnsFullContent()
    {
        _ws.WriteFileNoJournal("file.txt", "line one\nline two\nline three");

        var result = Read("file.txt");

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("1: line one", text);
        Assert.Contains("3: line three", text);
        Assert.DoesNotContain("[Read skipped:", text);
    }

    [Fact]
    public void ProcessReadFileCall_UnchangedSecondRead_ReturnsDedupStub()
    {
        _ws.WriteFileNoJournal("file.txt", "line one\nline two\nline three");
        Read("file.txt");

        var result = Read("file.txt");

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("[Read skipped: 'file.txt' was already read this session (lines 1-3) and is unchanged on disk", text);
        Assert.DoesNotContain("line one", text);
    }

    [Fact]
    public void ProcessReadFileCall_RangeParameters_Ignored_ReturnsWholeFile()
    {
        _ws.WriteFileNoJournal("file.txt", "line one\nline two\nline three");
        var result = Read("file.txt", startLine: 1, endLine: 2);

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("1: line one", text);
        Assert.Contains("3: line three", text);
    }

    [Fact]
    public void ProcessReadFileCall_RangeWithinCoverage_ReturnsDedupStub()
    {
        _ws.WriteFileNoJournal("file.txt", "line one\nline two\nline three");
        Read("file.txt", startLine: 1, endLine: 3);

        var result = Read("file.txt", startLine: 1, endLine: 2);

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("[Read skipped:", text);
    }

    [Fact]
    public void ProcessReadFileCall_RepeatedRead_ReturnsDedupStubEvenWithRanges()
    {
        _ws.WriteFileNoJournal("file.txt", "line one\nline two\nline three");
        Read("file.txt", startLine: 1, endLine: 2);

        var result = Read("file.txt", startLine: 2, endLine: 3);

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("[Read skipped:", text);
    }

    [Fact]
    public void ProcessReadFileCall_FileChangedOnDisk_ReturnsContentWithStaleNote()
    {
        _ws.WriteFileNoJournal("file.txt", "line one\nline two");
        Read("file.txt");

        File.WriteAllText(Path.Combine(_ws.Root, "file.txt"), "line one\nline two\nline three");
        var result = Read("file.txt");

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("[File changed on disk since last read — content below is current.]", text);
        Assert.Contains("3: line three", text);
        Assert.DoesNotContain("[Read skipped:", text);
    }

    [Fact]
    public void ProcessReadFileCall_WrittenBySessionThenRead_NoDedup()
    {
        // RecordWrite simulates the session's own Write tool: the content is
        // session-known, so a Read is never suppressed.
        _ws.WriteFile("file.txt", "line one\nline two\nline three");

        var result = Read("file.txt");

        string text = ContextManager.ExtractText(result.Content!);
        Assert.Contains("1: line one", text);
        Assert.DoesNotContain("[Read skipped:", text);
    }

    private ToolChatMessage Read(string relativePath, int? startLine = null, int? endLine = null)
    {
        string args = "{\"file_path\": \"" + relativePath + "\""
            + (startLine.HasValue ? ", \"start_line\": \"" + startLine + "\"" : "")
            + (endLine.HasValue ? ", \"end_line\": \"" + endLine + "\"" : "")
            + "}";
        var toolCall = ToolCallFactory.Create(ToolHandler.ReadFunctionName, args);
        return FileReadHandler.ProcessReadFileCall(toolCall)!;
    }
}
