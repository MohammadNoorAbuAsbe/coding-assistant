using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class AutopilotTests
{
    public AutopilotTests()
    {
        Configuration.LoadProviderConfigs();
    }

    private static void SetEnv(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    private static string ToolText(ToolChatMessage message)
    {
        Assert.NotNull(message.Content);
        return string.Join("", message.Content!.Select(p => p.Text ?? ""));
    }

    [Fact]
    public void BuildAutoAnswer_ContainsQuestionOptionsAndDirective()
    {
        var answer = QuestionHandler.BuildAutoAnswer("Which approach?", new List<ToolHandler.QuestionOption>
        {
            new() { label = "Option A", description = "Fast" },
            new() { label = "Option B", description = null }
        });

        Assert.Contains("Which approach?", answer);
        Assert.Contains("decide for yourself", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("autonomous mode", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1. Option A — Fast", answer);
        Assert.Contains("2. Option B", answer);
    }

    [Fact]
    public async Task QuestionCall_InAutopilotMode_ReturnsAutoAnswerInsteadOfAsking()
    {
        Autopilot.SetActiveForTesting(true);
        try
        {
            using var ws = new TempWorkspace();

            var toolCall = ToolCallFactory.Create(ToolHandler.QuestionFunctionName,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    question = "Should I refactor?",
                    options = new[]
                    {
                        new { label = "Yes", description = "Do it" },
                        new { label = "No", description = "Skip it" }
                    }
                }));

            var message = await ResponseHandler.ProcessSingleToolCallAsync(toolCall);

            string text = ToolText(message!);
            Assert.Contains("autonomous mode", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("decide for yourself", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Should I refactor?", text);
            Assert.Contains("1. Yes — Do it", text);
            Assert.Contains("2. No — Skip it", text);
            Assert.DoesNotContain("User did not provide an answer.", text);
        }
        finally
        {
            Autopilot.SetActiveForTesting(false);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAutopilotEnabled_ReadsEnv(string? value, bool expected)
    {
        SetEnv("AUTOPILOT", value);
        try
        {
            Assert.Equal(expected, Configuration.IsAutopilotEnabled());
        }
        finally
        {
            SetEnv("AUTOPILOT", null);
        }
    }

    [Fact]
    public void GetPrompt_AppendsMissionSectionInAutopilotMode()
    {
        string? originalSystemPrompt = Environment.GetEnvironmentVariable("SYSTEM_PROMPT");
        SetEnv("SYSTEM_PROMPT", null);
        Autopilot.SetActiveForTesting(true);
        try
        {
            string prompt = SystemPrompt.GetPrompt("ollama");
            Assert.Contains("AUTONOMOUS MODE", prompt);
            Assert.Contains("never run build or test commands", prompt, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Autopilot.SetActiveForTesting(false);
            SetEnv("SYSTEM_PROMPT", originalSystemPrompt);
        }
    }

    [Fact]
    public void BuildDirective_CommandsASingleWriteCall()
    {
        string directive = AutopilotSuggestions.BuildDirective();

        Assert.Contains("AUTOPILOT DIRECTIVE", directive);
        Assert.Contains("MUST be a single Edit, ApplyPatch, or Write tool call", directive);
        Assert.Contains("Do NOT call Read, Grep, Glob", directive);
        Assert.Contains("  - ", directive);
    }

    [Fact]
    public void BuildNoChangeCarryover_DemandsAChange()
    {
        string message = AutopilotSuggestions.BuildNoChangeCarryover();

        Assert.Contains("LAST CYCLE RESULT", message);
        Assert.Contains("MUST make at least one file change", message);
    }

    [Fact]
    public void GetPrompt_NoMissionSectionWhenNotInAutopilot()
    {
        string? originalSystemPrompt = Environment.GetEnvironmentVariable("SYSTEM_PROMPT");
        SetEnv("SYSTEM_PROMPT", null);
        Autopilot.SetActiveForTesting(false);
        try
        {
            string prompt = SystemPrompt.GetPrompt("ollama");
            Assert.DoesNotContain("AUTONOMOUS MODE", prompt);
        }
        finally
        {
            SetEnv("SYSTEM_PROMPT", originalSystemPrompt);
        }
    }
}
