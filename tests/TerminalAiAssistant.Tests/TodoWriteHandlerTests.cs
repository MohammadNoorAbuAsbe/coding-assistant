using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class TodoWriteHandlerTests
{
    public TodoWriteHandlerTests()
    {
        Configuration.LoadProviderConfigs();
    }

    private static string ToolText(ToolChatMessage message)
    {
        Assert.NotNull(message.Content);
        return string.Join("", message.Content!.Select(p => p.Text ?? ""));
    }

    private static async Task<ToolChatMessage?> RunAsync(object args)
    {
        var toolCall = ToolCallFactory.Create(ToolHandler.TodoWriteFunctionName, System.Text.Json.JsonSerializer.Serialize(args));
        return await ResponseHandler.ProcessSingleToolCallAsync(toolCall);
    }

    [Fact]
    public async Task TodoWrite_FormatsTaskList()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(new
        {
            todos = new[]
            {
                new { content = "First task", status = (string?)"completed", priority = (string?)"high" },
                new { content = "Second task", status = (string?)"in_progress", priority = (string?)null },
                new { content = "Third task", status = (string?)null, priority = (string?)null }
            }
        });

        string text = ToolText(message!);
        Assert.Contains("## Task List", text);
        Assert.Contains("**completed** (1):", text);
        Assert.Contains("✓ First task [high]", text);
        Assert.Contains("**in progress** (1):", text);
        Assert.Contains("→ Second task", text);
        Assert.Contains("**pending** (1):", text);
        Assert.Contains("· Third task", text);
        Assert.Contains("Progress: 1/3 tasks completed", text);
    }

    [Fact]
    public async Task TodoWrite_StatusesGroupedInFixedOrder()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(new
        {
            todos = new[]
            {
                new { content = "cancelled task", status = (string?)"cancelled", priority = (string?)null },
                new { content = "pending task", status = (string?)null, priority = (string?)null },
                new { content = "done task", status = (string?)"completed", priority = (string?)null },
                new { content = "doing task", status = (string?)"in_progress", priority = (string?)null }
            }
        });

        string text = ToolText(message!);
        int pending = text.IndexOf("**pending** (1):");
        int inProgress = text.IndexOf("**in progress** (1):");
        int completed = text.IndexOf("**completed** (1):");
        int cancelled = text.IndexOf("**cancelled** (1):");

        Assert.True(pending >= 0 && inProgress > pending && completed > inProgress && cancelled > completed,
            $"Expected fixed status order, got: {text}");
        Assert.Contains("✗ cancelled task", text);
        Assert.Contains("Progress: 1/4 tasks completed", text);
    }

    [Fact]
    public async Task TodoWrite_EmptyList_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(new { todos = Array.Empty<object>() });

        Assert.Contains("TodoWrite tool requires at least one todo item", ToolText(message!));
    }

    [Fact]
    public async Task TodoWrite_MissingTodosParameter_ReturnsError()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(new { });

        Assert.Contains("TodoWrite tool called with invalid arguments", ToolText(message!));
    }

    [Fact]
    public async Task TodoWrite_PriorityOnlyOnSpecifiedItems()
    {
        using var ws = new TempWorkspace();

        var message = await RunAsync(new
        {
            todos = new[]
            {
                new { content = "with priority", status = (string?)null, priority = (string?)"low" },
                new { content = "no priority", status = (string?)null, priority = (string?)null }
            }
        });

        string text = ToolText(message!);
        Assert.Contains("· with priority [low]", text);
        Assert.Contains("· no priority", text);
    }
}
