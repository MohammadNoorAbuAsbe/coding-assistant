using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class TaskHandler
{
    private static readonly AsyncLocal<int> _depth = new();

    private const string SubAgentSystemPrompt = @"
You are a coding assistant sub-agent working on a specific task delegated by the main agent.

## Available Tools
1. **Read** — Read a file. Shows content with line numbers like ""1: code"". Parameters: {""file_path"": ""<path>""}
2. **Write** — Write a file (creates dirs automatically). Parameters: {""file_path"": ""<path>"", ""content"": ""<content>""}
3. **Edit** — Edit a file by exact text replacement. Parameters: {""file_path"": ""<path>"", ""old_string"": ""<exact text>"", ""new_string"": ""<replacement text>""}
4. **EditLine** — Edit a file by replacing lines by number. Parameters: {""file_path"": ""<path>"", ""start_line"": ""<first line>"", ""end_line"": ""<last line>"", ""new_content"": ""<replacement>""}
5. **Bash** — Execute a shell command. Parameters: {""command"": ""<command>""}
6. **Glob** — Find files by glob pattern. Parameters: {""pattern"": ""<glob>""}. Optional: ""path""
7. **Grep** — Search file contents with ripgrep (regex). Parameters: {""pattern"": ""<regex>""}. Optional: ""path"", ""include"", ""exclude"", ""case_insensitive"", ""context_lines""
8. **Question** — Ask the user a question with multiple-choice options when you need a decision or clarification.
9. **WebFetch** — Fetch and return the contents of a URL.
10. **WebSearch** — Search the web for current information using Tavily.
11. **TodoWrite** — Create and maintain a structured task list.

## Rules
- Focus ONLY on your assigned task. Do not expand scope.
- You CANNOT launch sub-agents (Task tool is not available to you).
- When done, respond with a concise summary of what you accomplished.
- Do not ask the user for information you can discover yourself.";

    internal static async Task<ToolChatMessage?> ProcessTaskCallAsync(ChatToolCall toolCall)
    {
        return await ResponseHandler.ExecuteToolCallAsync<ToolHandler.TaskCall>(
            toolCall,
            "Expected format: {\"description\": \"<task description>\"}",
            "running sub-agent",
            async args =>
            {
                if (string.IsNullOrWhiteSpace(args.description))
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: Task tool missing required parameter 'description'.");
                }

                int depth = _depth.Value;
                int maxDepth = GetMaxSubAgentDepth();
                if (depth >= maxDepth)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, $"Error: Maximum sub-agent depth ({maxDepth}) reached. Cannot launch further sub-agents.");
                }

                _depth.Value = depth + 1;
                try
                {
                    return await ExecuteSubAgentAsync(args.description, toolCall);
                }
                finally
                {
                    _depth.Value = depth;
                }
            });
    }

    private static async Task<ToolChatMessage> ExecuteSubAgentAsync(string description, ChatToolCall parentToolCall)
    {
        var client = ChatService.CreateClient();
        var maxIterations = Configuration.GetMaxIterations();
        var contextWindowSize = Configuration.GetContextWindowSize();

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SubAgentSystemPrompt),
            new UserChatMessage(description)
        };

        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
            Console.Error.WriteLine($"\n[Sub-agent launched]");

        var result = await ChatOrchestrator.RunSubAgent(client, messages, maxIterations, contextWindowSize);

        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
            Console.Error.WriteLine($"[Sub-agent finished]");

        return new ToolChatMessage(parentToolCall.Id, result);
    }

    private static int GetMaxSubAgentDepth()
    {
        var value = Environment.GetEnvironmentVariable("MAX_SUB_AGENT_DEPTH");
        return int.TryParse(value, out var result) ? result : 3;
    }
}
