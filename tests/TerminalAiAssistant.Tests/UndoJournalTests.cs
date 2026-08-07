using System.Text.Json;
using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class UndoJournalTests
{
    public UndoJournalTests()
    {
        Configuration.LoadProviderConfigs();
        UndoJournal.Clear();
    }

    private static string ToolText(ToolChatMessage message)
    {
        Assert.NotNull(message.Content);
        return string.Join("", message.Content!.Select(p => p.Text ?? ""));
    }

    private static async Task<ToolChatMessage?> RunToolAsync(string name, object args)
    {
        var toolCall = ToolCallFactory.Create(name, JsonSerializer.Serialize(args));
        return await ResponseHandler.ProcessSingleToolCallAsync(toolCall);
    }

    [Fact]
    public async Task Write_RecordsBeforeImage_UndoRestoresPriorContent()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "original content\n");

        var write = await RunToolAsync(ToolHandler.WriteFunctionName,
            new { file_path = "file.txt", content = "new content" });
        Assert.Contains("Successfully wrote", ToolText(write!));
        Assert.Equal("new content", ws.ReadFile("file.txt"));

        var entry = UndoJournal.UndoLast();
        Assert.NotNull(entry);
        Assert.Equal(ToolHandler.WriteFunctionName, entry!.ToolName);
        Assert.True(entry.ExistedBefore);
        Assert.Equal("original content\n", entry.BeforeContent);
        Assert.Equal("original content\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task Write_NewFile_UndoDeletesIt()
    {
        using var ws = new TempWorkspace();

        var write = await RunToolAsync(ToolHandler.WriteFunctionName,
            new { file_path = "newfile.txt", content = "hello" });
        Assert.Contains("Successfully wrote", ToolText(write!));
        Assert.True(File.Exists(Path.Combine(ws.Root, "newfile.txt")));

        var entry = UndoJournal.UndoLast();
        Assert.NotNull(entry);
        Assert.False(entry!.ExistedBefore);
        Assert.False(File.Exists(Path.Combine(ws.Root, "newfile.txt")));
    }

    [Fact]
    public async Task Edit_RecordsBeforeImage_UndoReverts()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "one\ntwo\nthree\n");

        var edit = await RunToolAsync(ToolHandler.EditFunctionName,
            new { file_path = "file.txt", old_string = "two", new_string = "TWO" });
        Assert.Contains("Successfully edited", ToolText(edit!));
        Assert.Equal("one\nTWO\nthree\n", ws.ReadFile("file.txt"));

        var entry = UndoJournal.UndoLast();
        Assert.NotNull(entry);
        Assert.Equal(ToolHandler.EditFunctionName, entry!.ToolName);
        Assert.Equal("one\ntwo\nthree\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_ExistingFile_UndoRevertsAllHunks()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "aaa\nbbb\nccc\nddd\neee\n");

        var patch = await RunToolAsync(ToolHandler.ApplyPatchFunctionName,
            new { file_path = "file.txt", patch = "@@ -1,3 +1,3 @@\n aaa\n-bbb\n+BBB\n ccc\n@@ -4,2 +4,2 @@\n ddd\n-eee\n+EEE" });
        Assert.Contains("Successfully applied 2 hunk(s)", ToolText(patch!));
        Assert.Equal("aaa\nBBB\nccc\nddd\nEEE\n", ws.ReadFile("file.txt"));

        var entry = UndoJournal.UndoLast();
        Assert.NotNull(entry);
        Assert.Equal(ToolHandler.ApplyPatchFunctionName, entry!.ToolName);
        Assert.True(entry.ExistedBefore);
        Assert.Equal("aaa\nbbb\nccc\nddd\neee\n", ws.ReadFile("file.txt"));
    }

    [Fact]
    public async Task ApplyPatch_NewFile_UndoDeletesIt()
    {
        using var ws = new TempWorkspace();

        var patch = await RunToolAsync(ToolHandler.ApplyPatchFunctionName,
            new { file_path = "created.txt", patch = "@@\n+line1\n+line2" });
        Assert.Contains("Created new file", ToolText(patch!));
        Assert.True(File.Exists(Path.Combine(ws.Root, "created.txt")));

        var entry = UndoJournal.UndoLast();
        Assert.NotNull(entry);
        Assert.False(entry!.ExistedBefore);
        Assert.False(File.Exists(Path.Combine(ws.Root, "created.txt")));
    }

    [Fact]
    public async Task FailedToolCalls_DoNotRecordJournalEntries()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("file.txt", "one\ntwo\n");

        await RunToolAsync(ToolHandler.EditFunctionName,
            new { file_path = "file.txt", old_string = "missing text", new_string = "X" });

        Assert.Empty(UndoJournal.List());
    }

    [Fact]
    public async Task MultipleChanges_UndoPopsInLifoOrder()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "a1");
        ws.WriteFile("b.txt", "b1");

        await RunToolAsync(ToolHandler.WriteFunctionName, new { file_path = "a.txt", content = "a2" });
        await RunToolAsync(ToolHandler.WriteFunctionName, new { file_path = "b.txt", content = "b2" });

        var first = UndoJournal.UndoLast();
        Assert.Equal(Path.Combine(ws.Root, "b.txt"), first!.FullPath);
        Assert.Equal("b1", ws.ReadFile("b.txt"));
        Assert.Equal("a2", ws.ReadFile("a.txt"));

        var second = UndoJournal.UndoLast();
        Assert.Equal(Path.Combine(ws.Root, "a.txt"), second!.FullPath);
        Assert.Equal("a1", ws.ReadFile("a.txt"));
    }

    [Fact]
    public void UndoLast_EmptyJournal_ReturnsNull()
    {
        Assert.Null(UndoJournal.UndoLast());
    }

    [Fact]
    public async Task UndoAt_RevertsSpecificEntry_NewestFirstIndexing()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "a1");
        ws.WriteFile("b.txt", "b1");

        await RunToolAsync(ToolHandler.WriteFunctionName, new { file_path = "a.txt", content = "a2" });
        await RunToolAsync(ToolHandler.WriteFunctionName, new { file_path = "b.txt", content = "b2" });

        var entry = UndoJournal.UndoAt(1);
        Assert.NotNull(entry);
        Assert.Equal(Path.Combine(ws.Root, "a.txt"), entry!.FullPath);
        Assert.Equal("a1", ws.ReadFile("a.txt"));
        Assert.Equal("b2", ws.ReadFile("b.txt"));

        Assert.Equal(1, UndoJournal.List().Count);
    }

    [Fact]
    public void UndoAt_OutOfRange_ReturnsNullAndKeepsJournal()
    {
        using var ws = new TempWorkspace();
        UndoJournal.Record(Path.Combine(ws.Root, "a.txt"), "a1", existedBefore: true, "Write");

        Assert.Null(UndoJournal.UndoAt(5));
        Assert.Single(UndoJournal.List());
    }

    [Fact]
    public void List_ReturnsEntriesNewestFirst()
    {
        using var ws = new TempWorkspace();
        ws.WriteFile("a.txt", "a1");

        UndoJournal.Record(Path.Combine(ws.Root, "a.txt"), "a1", existedBefore: true, "Write");
        UndoJournal.Record(Path.Combine(ws.Root, "a.txt"), "a2", existedBefore: true, "Edit");

        var list = UndoJournal.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("Edit", list[0].ToolName);
        Assert.Equal("Write", list[1].ToolName);
    }

    [Fact]
    public void Record_RespectsUndoHistoryLimit()
    {
        using var ws = new TempWorkspace();
        ws.SaveEnv("UNDO_HISTORY_LIMIT");
        Environment.SetEnvironmentVariable("UNDO_HISTORY_LIMIT", "3");

        try
        {
            for (int i = 0; i < 5; i++)
            {
                UndoJournal.Record(Path.Combine(ws.Root, $"f{i}.txt"), "x", existedBefore: true, "Write");
            }

            Assert.Equal(3, UndoJournal.List().Count);
            var list = UndoJournal.List();
            Assert.Equal(Path.Combine(ws.Root, "f4.txt"), list[0].FullPath);
            Assert.Equal(Path.Combine(ws.Root, "f2.txt"), list[2].FullPath);
        }
        finally
        {
            ws.RestoreAllEnv();
        }
    }

    [Fact]
    public void Clear_EmptiesJournal()
    {
        using var ws = new TempWorkspace();
        UndoJournal.Record(Path.Combine(ws.Root, "a.txt"), "x", existedBefore: true, "Write");

        UndoJournal.Clear();

        Assert.Empty(UndoJournal.List());
        Assert.Null(UndoJournal.UndoLast());
    }
}
