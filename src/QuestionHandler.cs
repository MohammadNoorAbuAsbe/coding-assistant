using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class QuestionHandler
{
    internal static async Task<ToolChatMessage?> ProcessQuestionCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.QuestionCall>(
            toolCall,
            "Expected format: {\"question\": \"...\", \"options\": [...]}",
            "asking question",
            async args =>
            {
                if (args.question == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Question tool missing required parameter 'question'.");
                }

                if (args.options == null || args.options.Count == 0)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Question tool requires at least one option.");
                }

                if (Autopilot.IsActive)
                {
                    return new ToolChatMessage(toolCall.Id, BuildAutoAnswer(args.question, args.options));
                }

                string answer = await AppUi.AskQuestionAsync(
                    toolCall.Id, args.question, args.header, args.options,
                    args.allow_custom == "true", cancellationToken);

                return new ToolChatMessage(toolCall.Id, answer);
            });
    }

    internal static string BuildAutoAnswer(string question, List<ToolHandler.QuestionOption> options)
    {
        var sb = new StringBuilder();
        sb.Append("User is unavailable (autonomous mode). Decide for yourself and continue working — choose what you believe is best, and proceed. The question was: ");
        sb.Append(question);
        sb.Append("\nOptions:");
        for (int i = 0; i < options.Count; i++)
        {
            sb.Append($"\n{i + 1}. {options[i].label}");
            if (!string.IsNullOrEmpty(options[i].description))
            {
                sb.Append($" — {options[i].description}");
            }
        }
        return sb.ToString();
    }
}