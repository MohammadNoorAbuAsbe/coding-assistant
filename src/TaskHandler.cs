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
3. **Edit** — Edit a file by string replacement (tolerates leading/trailing whitespace and unicode differences per line — every line of old_string must exist in the file, never invent code you have not read; line-number prefixes like ""177: code"" are stripped automatically, so paste Read output verbatim). CRITICAL: Read the target lines first and copy old_string from the Read result; for method-level changes, read the ENTIRE method (wide range) before editing. Parameters: {""file_path"": ""<path>"", ""old_string"": ""<text>"", ""new_string"": ""<replacement text>""}
4. **ApplyPatch** — Apply a unified diff (patch) to a file, making multiple changes in one call. Hunks: @@ -start,count +start,count @@ followed by "" "" (context), ""-"" (removed), and ""+"" (added) lines. Parameters: {""file_path"": ""<path>"", ""patch"": ""<unified diff>""}
5. **Diff** — Preview the changes that WOULD be made to a file WITHOUT writing anything. The file is NEVER modified — the diff describes your PROPOSED new_content, not the current file; if it looks wrong, re-Read the file. Parameters: {""file_path"": ""<path>"", ""new_content"": ""<proposed content>""}
6. **PowerShell** — Execute a PowerShell command (runs via powershell.exe -Command). This is NOT bash: no ""&&"" (use "";""), no ""ls""/""cat""/""which""/""grep"" (use Get-ChildItem/Get-Content/Get-Command/Select-String or the Read/Grep tools). The backtick ` is the escape character, double quotes interpolate $vars, single quotes are verbatim. Parameters: {""command"": ""<command>""}
7. **Glob** — Find files by glob pattern. Parameters: {""pattern"": ""<glob>""}. Optional: ""path""
8. **Grep** — Search file contents with ripgrep (regex). Parameters: {""pattern"": ""<regex>""}. Optional: ""path"", ""include"", ""exclude"", ""case_insensitive"", ""context_lines""
9. **Question** — Ask the user a question with multiple-choice options when you need a decision or clarification.
10. **WebFetch** — Fetch and return the contents of a URL.
11. **WebSearch** — Search the web for current information using Tavily.
12. **TodoWrite** — Create and maintain a structured task list.

## Rules
- Focus ONLY on your assigned task. Do not expand scope.
- You CANNOT launch sub-agents (Task tool is not available to you).
- When done, respond with a concise summary of what you accomplished.
- Do not ask the user for information you can discover yourself.";

    internal static async Task<ToolChatMessage?> ProcessTaskCallAsync(ChatToolCall toolCall, CancellationToken cancellationToken = default)
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
                    return await ExecuteSubAgentAsync(args.description, toolCall, cancellationToken);
                }
                finally
                {
                    _depth.Value = depth;
                }
            });
    }

    private static async Task<ToolChatMessage> ExecuteSubAgentAsync(string description, ChatToolCall parentToolCall, CancellationToken cancellationToken)
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
            await Console.Error.WriteLineAsync($"\n[Sub-agent launched]");

        var result = await ChatOrchestrator.RunSubAgent(client, messages, maxIterations, contextWindowSize, cancellationToken);

        using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
            await Console.Error.WriteLineAsync($"[Sub-agent finished]");

        return new ToolChatMessage(parentToolCall.Id, result);
    }

    private static int GetMaxSubAgentDepth()
    {
        var value = Environment.GetEnvironmentVariable("MAX_SUB_AGENT_DEPTH");
        return int.TryParse(value, out var result) ? result : 3;
    }
}
