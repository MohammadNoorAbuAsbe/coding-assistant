using System.Text.Json;
using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ToolHandlerTests
{
    private static ChatTool GetTool(string name)
    {
        var options = ToolHandler.CreateCompletionOptions();
        var tool = options.Tools.FirstOrDefault(t => t.FunctionName == name);
        Assert.NotNull(tool);
        return tool;
    }

    private static JsonElement Parameters(string name)
    {
        var tool = GetTool(name);
        Assert.NotNull(tool.FunctionParameters);
        return JsonDocument.Parse(tool.FunctionParameters!.ToString()).RootElement;
    }

    private static string[] Required(string name)
    {
        var root = Parameters(name);
        return root.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(e => e.GetString()!).ToArray()
            : [];
    }

    [Fact]
    public void MainOptions_HasAllThirteenTools()
    {
        var options = ToolHandler.CreateCompletionOptions();

        var names = options.Tools.Select(t => t.FunctionName).OrderBy(n => n).ToArray();
        var expected = new[]
        {
            "ApplyPatch", "Bash", "Diff", "Edit", "Glob", "Grep", "Question", "Read",
            "Task", "TodoWrite", "WebFetch", "WebSearch", "Write"
        };

        Assert.Equal(expected, names);
    }

    [Fact]
    public void SubAgentOptions_HasTwelveTools_WithoutTask()
    {
        var options = ToolHandler.CreateSubAgentCompletionOptions();

        Assert.DoesNotContain(options.Tools, t => t.FunctionName == ToolHandler.TaskFunctionName);
        Assert.Equal(12, options.Tools.Count);
        Assert.Contains(options.Tools, t => t.FunctionName == ToolHandler.TodoWriteFunctionName);
    }

    [Fact]
    public void ToolNames_AreUnique()
    {
        var options = ToolHandler.CreateCompletionOptions();
        var duplicates = options.Tools.GroupBy(t => t.FunctionName).Where(g => g.Count() > 1);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void AllTools_HaveNonEmptyDescriptions()
    {
        foreach (var tool in ToolHandler.CreateCompletionOptions().Tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.FunctionDescription), $"{tool.FunctionName} has no description");
        }
    }

    [Theory]
    [InlineData(ToolHandler.ReadFunctionName, "file_path")]
    [InlineData(ToolHandler.WriteFunctionName, "file_path", "content")]
    [InlineData(ToolHandler.EditFunctionName, "file_path", "old_string", "new_string")]
    [InlineData(ToolHandler.ApplyPatchFunctionName, "file_path", "patch")]
    [InlineData(ToolHandler.DiffFunctionName, "file_path", "new_content")]
    [InlineData(ToolHandler.BashFunctionName, "command")]
    [InlineData(ToolHandler.GlobFunctionName, "pattern")]
    [InlineData(ToolHandler.GrepFunctionName, "pattern")]
    [InlineData(ToolHandler.WebFetchFunctionName, "url")]
    [InlineData(ToolHandler.WebSearchFunctionName, "query")]
    [InlineData(ToolHandler.QuestionFunctionName, "question", "options")]
    [InlineData(ToolHandler.TaskFunctionName, "description")]
    [InlineData(ToolHandler.TodoWriteFunctionName, "todos")]
    public void Tool_RequiredParameters(string toolName, params string[] expected)
    {
        var actual = Required(toolName);
        Assert.Equal(expected.OrderBy(e => e), actual.OrderBy(a => a));
    }

    [Fact]
    public void QuestionTool_OptionsArrayWithLabelRequired()
    {
        var root = Parameters(ToolHandler.QuestionFunctionName);
        var options = root.GetProperty("properties").GetProperty("options");

        Assert.Equal("array", options.GetProperty("type").GetString());
        var items = options.GetProperty("items");
        Assert.Equal("object", items.GetProperty("type").GetString());
        var required = items.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("label", required);
        Assert.Contains(items.GetProperty("properties").EnumerateObject(), p => p.Name == "label");
    }

    [Fact]
    public void TodoWriteTool_TodosArray()
    {
        var root = Parameters(ToolHandler.TodoWriteFunctionName);
        var todos = root.GetProperty("properties").GetProperty("todos");

        Assert.Equal("array", todos.GetProperty("type").GetString());
        var items = todos.GetProperty("items");
        Assert.Equal("object", items.GetProperty("type").GetString());
        var required = items.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Contains("content", required);
    }

    [Fact]
    public void AllTools_HaveStringTypeParameters()
    {
        foreach (var tool in ToolHandler.CreateCompletionOptions().Tools)
        {
            var root = JsonDocument.Parse(tool.FunctionParameters!.ToString()).RootElement;
            var properties = root.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
            {
                string type = property.Value.GetProperty("type").GetString() ?? "";
                Assert.True(type is "string" or "array", $"{tool.FunctionName}.{property.Name} has type '{type}'");
            }
        }
    }
}
