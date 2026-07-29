using System.Text.Json;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class QuestionHandler
{
    internal static ToolChatMessage? ProcessQuestionCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.QuestionCall>(
            toolCall,
            "Expected format: {\"question\": \"...\", \"options\": [...]}",
            "asking question",
            args =>
            {
                if (args.question == null)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Question tool missing required parameter 'question'.");
                }

                if (args.options == null || args.options.Count == 0)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Question tool requires at least one option.");
                }

                string answer = AskUser(args.question, args.header, args.options, args.allow_custom == "true");

                return new ToolChatMessage(toolCall.Id, answer);
            });
    }

    private static string AskUser(string question, string? header, List<ToolHandler.QuestionOption> options, bool allowCustom)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("--- Question ---");

        if (!string.IsNullOrEmpty(header))
        {
            Console.Error.WriteLine($"[{header}]");
        }

        Console.Error.WriteLine(question);
        Console.Error.WriteLine();

        for (int i = 0; i < options.Count; i++)
        {
            var opt = options[i];
            Console.Error.WriteLine($"  {i + 1}. {opt.label}");
            if (!string.IsNullOrEmpty(opt.description))
            {
                Console.Error.WriteLine($"     {opt.description}");
            }
        }

        if (allowCustom)
        {
            Console.Error.WriteLine($"  {options.Count + 1}. [Type your own answer]");
        }

        Console.Error.WriteLine();
        Console.Error.Write("Your choice (enter number");

        if (allowCustom)
        {
            Console.Error.Write(" or type your answer");
        }

        Console.Error.Write("): ");

        string? input = Console.ReadLine();
        Console.Error.WriteLine();

        if (string.IsNullOrWhiteSpace(input))
        {
            return "User did not provide an answer.";
        }

        if (int.TryParse(input.Trim(), out int choice) && choice >= 1 && choice <= options.Count)
        {
            var selected = options[choice - 1];
            return $"User selected option {choice}: {selected.label}\nDescription: {selected.description ?? "(no description)"}";
        }

        if (allowCustom && int.TryParse(input.Trim(), out int customChoice) && customChoice == options.Count + 1)
        {
            Console.Error.Write("Enter your answer: ");
            string? customAnswer = Console.ReadLine();
            Console.Error.WriteLine();
            return $"User provided custom answer: {customAnswer ?? "(empty)"}";
        }

        if (allowCustom)
        {
            return $"User provided custom answer: {input.Trim()}";
        }

        return $"User entered invalid input: '{input.Trim()}'. Valid choices are 1-{options.Count}.";
    }
}