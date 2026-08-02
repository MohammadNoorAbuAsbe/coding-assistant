using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ResponseHandlerToolTests
{
    public ResponseHandlerToolTests()
    {
        Configuration.LoadProviderConfigs();
    }

    private static string ToolText(ToolChatMessage message)
    {
        Assert.NotNull(message.Content);
        return string.Join("", message.Content!.Select(p => p.Text ?? ""));
    }

    private static async Task<ToolChatMessage?> RunAsync(string name, object args)
    {
        var toolCall = ToolCallFactory.Create(name, System.Text.Json.JsonSerializer.Serialize(args));
        return await ResponseHandler.ProcessSingleToolCallAsync(toolCall);
    }

    [Fact]
    public async Task Read_ExistingFile_ReturnsLineNumberedContent()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "hello\nworld\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt" });

        string text = ToolText(message!);
        Assert.Contains("1: hello", text);
        Assert.Contains("2: world", text);
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "missing.txt" });

        Assert.Contains("Error reading file", ToolText(message!));
    }

    [Fact]
    public async Task Read_PathOutsideWorkspace_ReturnsError()
    {
        using var ws = new TempWorkspace();
        string outside = Path.Combine(Path.GetTempPath(), "taa-rh-outside", "f.txt");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = outside });

        Assert.Contains("Access denied", ToolText(message!));
    }

    [Fact]
    public async Task Write_CreatesFileAndDirectories()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.WriteFunctionName, new { file_path = "sub/dir/f.txt", content = "abc" });

        Assert.Contains("Successfully wrote content to sub/dir/f.txt", ToolText(message!));
        Assert.Equal("abc", ws.ReadFile("sub/dir/f.txt"));
    }

    [Fact]
    public async Task Write_PathOutsideWorkspace_ReturnsError()
    {
        using var ws = new TempWorkspace();
        string outside = Path.Combine(Path.GetTempPath(), "taa-rh-out2", "f.txt");

        var message = await RunAsync(ToolHandler.WriteFunctionName, new { file_path = outside, content = "x" });

        Assert.Contains("Access denied", ToolText(message!));
    }

    [Fact]
    public async Task Write_MissingContentParameter_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.WriteFunctionName, new { file_path = "f.txt" });

        Assert.Contains("called with invalid arguments", ToolText(message!));
    }

    [Fact]
    public async Task Edit_ExactMatch_UpdatesFile()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "bar", new_string = "BAZ" });

        Assert.Contains("Successfully edited file.txt", ToolText(message!));
        Assert.Equal("foo\nBAZ\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_FuzzyWhitespace_ReportsStrategy()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "  padded line  \n  other  \n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "padded line\nother", new_string = "changed line\nother" });

        string text = ToolText(message!);
        Assert.Contains("Successfully edited file.txt", text);
        Assert.Contains("matched using NormalizedWhitespace comparison", text);
        Assert.Equal("  changed line\nother\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_LcsMatch_ReportsConfidence()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "a\nb\na\nc\nend\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "a\nb\nc", new_string = "A\nB\nC" });

        string text = ToolText(message!);
        Assert.Contains("Successfully edited file.txt", text);
        Assert.Contains("matched using LCS comparison, confidence", text);
        Assert.Equal("A\nB\nC\nend\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_NoMatch_ReturnsErrorSuggestingApplyPatch()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "zzz", new_string = "x" });

        string text = ToolText(message!);
        Assert.Contains("Edit tool could not find the specified 'old_string'", text);
        Assert.Contains("ApplyPatch", text);
        Assert.Equal("foo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_AmbiguousMatch_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\nxxx\nfoo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "foo\nbar", new_string = "baz" });

        Assert.Contains("Edit tool could not find the specified 'old_string'", ToolText(message!));
        Assert.Equal("foo\nbar\nxxx\nfoo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_MissingFile_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "missing.txt", old_string = "a", new_string = "b" });

        Assert.Contains("Error: file not found 'missing.txt'", ToolText(message!));
    }

    [Fact]
    public async Task UnknownFunction_ReturnsErrorListingAvailable()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync("Nope", new { });

        string text = ToolText(message!);
        Assert.Contains("unknown function 'Nope'", text);
        Assert.Contains("Available functions: Read, Write", text);
    }

    [Fact]
    public async Task InvalidJsonArguments_ReturnsFormatError()
    {
        using var ws = new TempWorkspace();

        var toolCall = ToolCallFactory.Create(ToolHandler.ReadFunctionName, "not json {{{");
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("called with invalid arguments", ToolText(message!));
    }

    [Fact]
    public async Task Glob_RecursivePattern_FindsAll()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "x");
        ws.WriteFile("b.txt", "x");
        ws.WriteFile("sub/c.txt", "x");

        var message = await RunAsync(ToolHandler.GlobFunctionName, new { pattern = "**/*.txt" });

        string text = ToolText(message!);
        Assert.Contains(Path.Combine(ws.Root, "a.txt"), text);
        Assert.Contains(Path.Combine(ws.Root, "b.txt"), text);
        Assert.Contains(Path.Combine(ws.Root, "sub", "c.txt"), text);
    }

    [Fact]
    public async Task Glob_NoMatches_ReturnsMessage()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "x");

        var message = await RunAsync(ToolHandler.GlobFunctionName, new { pattern = "**/*.md" });

        Assert.Contains("No files matching pattern", ToolText(message!));
    }

    [Fact]
    public async Task Glob_MissingDirectory_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.GlobFunctionName, new { pattern = "*", path = "does-not-exist" });

        Assert.Contains("does not exist", ToolText(message!));
    }

    [Fact]
    public async Task Glob_OutsideWorkspace_ReturnsError()
    {
        using var ws = new TempWorkspace();
        string outside = Path.Combine(Path.GetTempPath(), "taa-rh-glob-out");

        var message = await RunAsync(ToolHandler.GlobFunctionName, new { pattern = "*", path = outside });

        Assert.Contains("Access denied", ToolText(message!));
    }

    [Fact]
    public async Task Grep_FindsMatches_WhenRipgrepAvailable()
    {
        if (RipgrepHelper.FindRipgrep() == null)
        {
            return;
        }

        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "alpha\nTODO: fix this\nomega\n");

        var message = await RunAsync(ToolHandler.GrepFunctionName, new { pattern = "TODO" });

        string text = ToolText(message!);
        Assert.Contains("file.txt", text);
        Assert.Contains("TODO: fix this", text);
    }

    [Fact]
    public async Task Grep_NoMatches_ReturnsMessage()
    {
        if (RipgrepHelper.FindRipgrep() == null)
        {
            return;
        }

        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "alpha\nbeta\n");

        var message = await RunAsync(ToolHandler.GrepFunctionName, new { pattern = "zzz" });

        Assert.Contains("No matches found for pattern: zzz", ToolText(message!));
    }

    [Fact]
    public async Task Grep_InvalidRegex_ReturnsError()
    {
        if (RipgrepHelper.FindRipgrep() == null)
        {
            return;
        }

        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "alpha\n");

        var message = await RunAsync(ToolHandler.GrepFunctionName, new { pattern = "[" });

        Assert.Contains("ripgrep invalid pattern", ToolText(message!));
    }

    [Fact]
    public async Task Grep_CaseInsensitive_UsesFlag()
    {
        if (RipgrepHelper.FindRipgrep() == null)
        {
            return;
        }

        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "Alpha\nbeta\n");

        var message = await RunAsync(ToolHandler.GrepFunctionName, new { pattern = "alpha", case_insensitive = "true" });

        Assert.Contains("Alpha", ToolText(message!));
    }

    [Fact]
    public async Task PowerShell_Success_ReturnsStdout()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.PowershellFunctionName, new { command = "echo hello" });

        Assert.Equal("hello", ToolText(message!).Trim());
    }

    [Fact]
    public async Task PowerShell_Failure_ReturnsExitCode()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.PowershellFunctionName, new { command = "exit 1" });

        string text = ToolText(message!);
        Assert.Contains("Exit code: 1", text);
    }

    [Fact]
    public async Task PowerShell_Timeout_ReturnsTimeoutError()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("BASH_TIMEOUT");
        Environment.SetEnvironmentVariable("BASH_TIMEOUT", "200");
        string command = "Start-Sleep -Seconds 10";

        var message = await RunAsync(ToolHandler.PowershellFunctionName, new { command });

        Assert.Contains("timed out after", ToolText(message!));
    }

    [Fact]
    public async Task PowerShell_CommandWithQuotes_Works()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.PowershellFunctionName, new { command = "echo 'hello world'" });

        Assert.Equal("hello world", ToolText(message!).Trim());
    }
}
