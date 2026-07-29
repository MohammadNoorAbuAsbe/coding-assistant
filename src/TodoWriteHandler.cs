using System.Text;
using System.Text.Json;
using OpenAI.Chat;

namespace TerminalAiAssistant;

internal static class TodoWriteHandler
{
    private static List<TodoItem> _todos = new();

    internal static ToolChatMessage? ProcessTodoWriteCall(ChatToolCall toolCall)
    {
        return ResponseHandler.ExecuteToolCall<ToolHandler.TodoWriteCall>(
            toolCall,
            "Expected format: {\"todos\": [{\"content\": \"...\", \"status\": \"...\", \"priority\": \"...\"}]}",
            "updating task list",
            args =>
            {
                if (args.todos == null || args.todos.Count == 0)
                {
                    return ResponseHandler.CreateErrorResult(toolCall, "Error: TodoWrite tool requires at least one todo item.");
                }

                _todos = args.todos;
                return new ToolChatMessage(toolCall.Id, FormatTodoList(_todos));
            });
    }

    private static string FormatTodoList(List<TodoItem> todos)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Task List");

        var byStatus = todos.GroupBy(t => t.Status ?? "pending").ToDictionary(g => g.Key, g => g.ToList());

        string[] statusOrder = ["pending", "in_progress", "completed", "cancelled"];
        foreach (var status in statusOrder)
        {
            if (!byStatus.TryGetValue(status, out var items)) continue;
            string icon = status switch
            {
                "completed" => "✓",
                "in_progress" => "→",
                "cancelled" => "✗",
                _ => "·"
            };
            sb.AppendLine($"\n**{status.Replace("_", " ")}** ({items.Count}):");
            foreach (var item in items)
            {
                string pri = !string.IsNullOrEmpty(item.Priority) ? $" [{item.Priority}]" : "";
                sb.AppendLine($"  {icon} {item.Content}{pri}");
            }
        }

        int completed = byStatus.GetValueOrDefault("completed", new()).Count;
        int total = todos.Count;
        sb.AppendLine($"\nProgress: {completed}/{total} tasks completed");
        return sb.ToString();
    }
}

public class TodoItem
{
    public string Content { get; set; } = "";
    public string? Status { get; set; }
    public string? Priority { get; set; }
}
