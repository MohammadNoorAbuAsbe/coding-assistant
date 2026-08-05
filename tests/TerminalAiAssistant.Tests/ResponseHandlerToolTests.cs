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
    public async Task Read_LineRange_ReturnsOnlyRequestedLines()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt", start_line = "5", end_line = "7" });

        string text = ToolText(message!);
        Assert.Contains("5: line 5", text);
        Assert.Contains("7: line 7", text);
        Assert.DoesNotContain("4: line 4", text);
        Assert.DoesNotContain("8: line 8", text);
    }

    [Fact]
    public async Task Read_InvalidLineNumbers_FallBackToFileBounds()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "one\ntwo\nthree\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt", start_line = "abc", end_line = "999" });

        string text = ToolText(message!);
        Assert.Contains("1: one", text);
        Assert.Contains("3: three", text);
    }

    [Fact]
    public async Task Read_StartLineOnly_ExpandsToEnclosingMethod()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt",
            "class A\n{\n" +
            "    public void Foo()\n" +
            "    {\n" +
            "        int x = 1;\n" +
            "        if (x > 0)\n" +
            "        {\n" +
            "            x++;\n" +
            "        }\n" +
            "    }\n" +
            "}\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt", start_line = "5" });

        string text = ToolText(message!);
        Assert.Contains("expanded to enclosing method: lines 3-10", text);
        Assert.Contains("3:     public void Foo()", text);
        Assert.Contains("10:     }", text);
        Assert.DoesNotContain("11: }", text);
    }

    [Fact]
    public async Task Read_StartLineOnly_MultiLineSignature_Expands()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt",
            "    public static async Task<int> Foo(\n" +
            "        int a,\n" +
            "        int b)\n" +
            "    {\n" +
            "        return a + b;\n" +
            "    }\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt", start_line = "5" });

        string text = ToolText(message!);
        Assert.Contains("expanded to enclosing method: lines 1-6", text);
        Assert.Contains("1:     public static async Task<int> Foo(", text);
        Assert.Contains("6:     }", text);
    }

    [Fact]
    public async Task Read_WithEndLine_NoMethodExpansion()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt",
            "class A\n{\n" +
            "    public void Foo()\n" +
            "    {\n" +
            "        int x = 1;\n" +
            "    }\n" +
            "}\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt", start_line = "5", end_line = "5" });

        string text = ToolText(message!);
        Assert.DoesNotContain("expanded to enclosing method", text);
        Assert.Contains("5:         int x = 1;", text);
        Assert.DoesNotContain("3:     public void Foo()", text);
    }

    [Fact]
    public async Task Read_LargeFile_TruncatesAndSuggestsRanges()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("MAX_TOOL_RESULT_TOKENS");
        ws.SaveEnv("CONTEXT_WINDOW_SIZE");
        Environment.SetEnvironmentVariable("CONTEXT_WINDOW_SIZE", "1000");
        Environment.SetEnvironmentVariable("MAX_TOOL_RESULT_TOKENS", null);
        Configuration.SetProvider("ollama");

        ws.WriteFile("big.txt", string.Join("\n", Enumerable.Range(1, 500).Select(i => $"line number {i} with some extra words here")) + "\n");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "big.txt" });

        string text = ToolText(message!);
        Assert.Contains("[truncated: showing lines 1-", text);
        Assert.Contains("Use Read with start_line/end_line", text);
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
    public async Task Edit_Success_ReportsLineSpanAndLineCounts()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "line1\nline2\nline3\nline4\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "line2\nline3", new_string = "A\nB\nC" });

        string text = ToolText(message!);
        Assert.Contains("Edit location: lines 2-3 (2 line(s) replaced with 3 line(s)).", text);
        Assert.Contains("File now has 6 lines (was 5).", text);
        Assert.Equal("line1\nA\nB\nC\nline4\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_FuzzyMatch_IncludesCaution()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "  padded line  \n  other  \n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "padded line\nother", new_string = "changed line\nother" });

        string text = ToolText(message!);
        Assert.Contains("CAUTION: old_string matched approximately", text);
        Assert.Contains("matched using NormalizedWhitespace comparison", text);
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
    public async Task Edit_NoMatch_ReturnsErrorAdvisingVerbatimRetry()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "zzz", new_string = "x" });

        string text = ToolText(message!);
        Assert.Contains("Edit tool could not find the specified 'old_string'", text);
        Assert.Contains("copied verbatim from the Read output", text);
        Assert.Equal("foo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_NoMatch_ReturnsClosestRegionSuggestion()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\nbaz\nqux\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "barzz", new_string = "x" });

        string text = ToolText(message!);
        Assert.Contains("Closest region in the file", text);
        Assert.Contains("2: bar", text);
        Assert.Equal("foo\nbar\nbaz\nqux\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_ReplaceAll_ReplacesEveryExactOccurrence()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\nfoo\nbaz\nfoo\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "foo", new_string = "FOO", replace_all = "true" });

        string text = ToolText(message!);
        Assert.Contains("Successfully edited file.txt", text);
        Assert.Contains("replaced 3 occurrence(s)", text);
        Assert.Equal("FOO\nbar\nFOO\nbaz\nFOO\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_ReplaceAll_NoExactMatches_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "zzz", new_string = "x", replace_all = "true" });

        string text = ToolText(message!);
        Assert.Contains("Edit tool could not find the specified 'old_string'", text);
        Assert.Equal("foo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_ReplaceAll_WithoutFlag_StillRejectsAmbiguousMatches()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\nfoo\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "foo", new_string = "FOO" });

        string text = ToolText(message!);
        Assert.Contains("Error", text);
        Assert.Contains("Add more surrounding context", text);
        Assert.Equal("foo\nbar\nfoo\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_ReplaceAll_OnCRLFFile_PreservesLineEndings()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\r\nfoo\r\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "foo", new_string = "FOO", replace_all = "true" });

        string text = ToolText(message!);
        Assert.Contains("replaced 2 occurrence(s)", text);
        Assert.Equal("FOO\r\nFOO\r\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_NoMatch_NoSuggestionWhenNothingSimilar()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "zzz\nzzz", new_string = "x" });

        string text = ToolText(message!);
        Assert.DoesNotContain("Closest region in the file", text);
    }

    [Fact]
    public async Task Edit_ReadOutputWithLineNumberPrefixes_Succeeds()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\n  padded  \nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "2:   padded  ", new_string = "changed" });

        Assert.Contains("Successfully edited file.txt", ToolText(message!));
        Assert.Equal("foo\nchanged\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_NeverReadFile_Refuses()
    {
        using var ws = new TempWorkspace();
        FileStateJournal.Clear();
        System.IO.File.WriteAllText(System.IO.Path.Combine(ws.Root, "file.txt"), "foo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "bar", new_string = "BAZ" });

        string text = ToolText(message!);
        Assert.Contains("was not read in this session", text);
        Assert.Contains("Use the Read tool to read the file first", text);
        Assert.Equal("foo\nbar\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_AfterRead_Succeeds()
    {
        using var ws = new TempWorkspace();
        FileStateJournal.Clear();
        ws.WriteFile("file.txt", "foo\nbar\n");

        await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "file.txt" });
        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "bar", new_string = "BAZ" });

        Assert.Contains("Successfully edited file.txt", ToolText(message!));
        Assert.Equal("foo\nBAZ\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_StaleFile_WarnsAndApplies()
    {
        using var ws = new TempWorkspace();
        FileStateJournal.Clear();
        ws.WriteFile("file.txt", "foo\nbar\n");
        System.IO.File.WriteAllText(System.IO.Path.Combine(ws.Root, "file.txt"), "foo\nCHANGED\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "CHANGED", new_string = "changed" });

        string text = ToolText(message!);
        Assert.Contains("changed on disk since the session last read or wrote it", text);
        Assert.Contains("Successfully edited file.txt", text);
        Assert.Equal("foo\nchanged\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_DisproportionateMatch_Refuses()
    {
        using var ws = new TempWorkspace();
        FileStateJournal.Clear();
        var lines = new List<string> { "x" };
        for (int i = 0; i < 8; i++) lines.Add($"filler {i}");
        lines.Add("y");
        ws.WriteFile("file.txt", string.Join("\n", lines) + "\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "x\ny", new_string = "z" });

        string text = ToolText(message!);
        Assert.Contains("matched span is much larger than old_string", text);
        Assert.Equal(string.Join("\n", lines) + "\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_CRLFFile_PreservesLineEndings()
    {
        using var ws = new TempWorkspace();
        FileStateJournal.Clear();
        ws.WriteFile("file.txt", "a\r\nb\r\nc\r\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "a\nb", new_string = "A\nB" });

        string text = ToolText(message!);
        Assert.Contains("Successfully edited file.txt", text);
        Assert.DoesNotContain("CAUTION", text);
        Assert.Equal("A\r\nB\r\nc\r\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Edit_DeleteLine_RemovesTrailingNewline()
    {
        using var ws = new TempWorkspace();
        FileStateJournal.Clear();
        ws.WriteFile("file.txt", "line1\nline2\nline3\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "line2", new_string = "" });

        Assert.Contains("Successfully edited file.txt", ToolText(message!));
        Assert.Equal("line1\nline3\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Read_MissingFile_SuggestsSimilarNames()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("missing2.txt", "x");
        ws.WriteFile("other.txt", "y");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "missing.txt" });

        string text = ToolText(message!);
        Assert.Contains("was not found", text);
        Assert.Contains("Did you mean one of these?", text);
        Assert.Contains("missing2.txt", text);
    }

    [Fact]
    public async Task Read_BinaryFile_Rejected()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("data.bin", "not really binary but the extension says so");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "data.bin" });

        Assert.Contains("binary file", ToolText(message!));
    }

    [Fact]
    public async Task Read_Directory_ListsEntries()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("subdir\\one.txt", "1");
        ws.WriteFile("subdir\\two.cs", "2");

        var message = await RunAsync(ToolHandler.ReadFunctionName, new { file_path = "subdir" });

        string text = ToolText(message!);
        Assert.Contains("Directory listing of subdir", text);
        Assert.Contains("one.txt", text);
        Assert.Contains("two.cs", text);
    }

    [Fact]
    public async Task Edit_AmbiguousMatch_ReturnsError()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "foo\nbar\nxxx\nfoo\nbar\n");

        var message = await RunAsync(ToolHandler.EditFunctionName, new { file_path = "file.txt", old_string = "foo\nbar", new_string = "baz" });

        Assert.Contains("matches the file in more than one place", ToolText(message!));
        Assert.Contains("make it unique", ToolText(message!));
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
    public async Task Edit_DoubleEscapedStrings_AreRepaired()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "line1\nline2\n");

        string json = $"{{\"file_path\":\"file.txt\",\"old_string\":{System.Text.Json.JsonSerializer.Serialize("line1\nline2")},\"new_string\":{System.Text.Json.JsonSerializer.Serialize("say \\\"hi\\\"\nline2")}}}";
        var toolCall = ToolCallFactory.Create(ToolHandler.EditFunctionName, json);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Successfully edited file.txt", ToolText(message!));
        Assert.Equal("say \"hi\"\nline2\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Write_DoubleEscapedContent_IsRepaired()
    {
        using var ws = new TempWorkspace();

        string json = $"{{\"file_path\":\"f.txt\",\"content\":{System.Text.Json.JsonSerializer.Serialize("say \\\"hi\\\"")}}}";
        var toolCall = ToolCallFactory.Create(ToolHandler.WriteFunctionName, json);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Successfully wrote content to f.txt", ToolText(message!));
        Assert.Equal("say \"hi\"", ws.ReadFile("f.txt"));
    }

    [Fact]
    public async Task Write_FullyDoubleEncodedJsonArguments_AreDecoded()
    {
        using var ws = new TempWorkspace();

        string inner = System.Text.Json.JsonSerializer.Serialize(new { file_path = "f.txt", content = "hello" });
        string json = System.Text.Json.JsonSerializer.Serialize(inner);
        var toolCall = ToolCallFactory.Create(ToolHandler.WriteFunctionName, json);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Successfully wrote content to f.txt", ToolText(message!));
        Assert.Equal("hello", ws.ReadFile("f.txt"));
    }

    [Fact]
    public async Task ApplyPatch_DoubleEscapedAddedLines_AreRepaired()
    {
        using var ws = new TempWorkspace();

        string json = $"{{\"file_path\":\"f.txt\",\"patch\":{System.Text.Json.JsonSerializer.Serialize("@@\n+say \\\"hi\\\"")}}}";
        var toolCall = ToolCallFactory.Create(ToolHandler.ApplyPatchFunctionName, json);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Created new file f.txt", ToolText(message!));
        Assert.Equal("say \"hi\"\n", ws.ReadFile("f.txt"));
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
    public async Task Write_ArgsInCodeFences_AreRepaired()
    {
        using var ws = new TempWorkspace();

        string raw = "```json\n{\"file_path\": \"f.txt\", \"content\": \"hello\"}\n```";
        var toolCall = ToolCallFactory.Create(ToolHandler.WriteFunctionName, raw);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Successfully wrote content to f.txt", ToolText(message!));
        Assert.Equal("hello", ws.ReadFile("f.txt"));
    }

    [Fact]
    public async Task Write_SingleQuotedUnquotedKeys_AreRepaired()
    {
        using var ws = new TempWorkspace();

        string raw = "{file_path: 'f.txt', content: 'hello',}";
        var toolCall = ToolCallFactory.Create(ToolHandler.WriteFunctionName, raw);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("Successfully wrote content to f.txt", ToolText(message!));
        Assert.Equal("hello", ws.ReadFile("f.txt"));
    }

    [Fact]
    public async Task Read_TruncatedArgs_ProgressiveRepairSalvages()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("f.txt", "hello\nworld\n");

        string raw = "{\"file_path\": \"f.txt\", \"start_line\": \"2";
        var toolCall = ToolCallFactory.Create(ToolHandler.ReadFunctionName, raw);
        var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

        Assert.Contains("1: hello", ToolText(message!));
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

    [Fact]
    public async Task PowerShell_CommandWithDoubleQuotes_Works()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(ToolHandler.PowershellFunctionName, new { command = "Write-Output \"hello world\"" });

        Assert.Equal("hello world", ToolText(message!).Trim());
    }

    [Fact]
    public async Task PowerShell_CommandWithQuotesAndSubexpression_Works()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("data.txt", "abc");

        var message = await RunAsync(ToolHandler.PowershellFunctionName,
            new { command = "$b = [System.IO.File]::ReadAllBytes(\"data.txt\"); Write-Output \"count=$($b.Count)\"" });

        Assert.Contains("count=3", ToolText(message!));
    }
}
