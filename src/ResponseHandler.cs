using System.Diagnostics;
using System.Text.Json;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ResponseHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static List<ChatMessage> ProcessToolCalls(ChatCompletion response)
    {
        return ProcessToolCalls(response.ToolCalls);
    }

    public static List<ChatMessage> ProcessToolCalls(IReadOnlyList<ChatToolCall>? toolCalls)
    {
        var toolResultMessages = new List<ChatMessage>();

        if (toolCalls == null || toolCalls.Count == 0)
        {
            return toolResultMessages;
        }

        foreach (var toolCall in toolCalls)
        {
            var result = ProcessToolCall(toolCall);
            if (result != null)
            {
                toolResultMessages.Add(result);
            }
        }

        return toolResultMessages;
    }

    private static ToolChatMessage? ProcessToolCall(ChatToolCall toolCall)
    {
        if (string.IsNullOrEmpty(toolCall.FunctionName))
        {
            return CreateErrorResult(toolCall, "Error: received tool call with no function name.");
        }

        return toolCall.FunctionName switch
        {
            ToolHandler.ReadFunctionName => ProcessReadFileCall(toolCall),
            ToolHandler.WriteFunctionName => ProcessWriteFileCall(toolCall),
            ToolHandler.BashFunctionName => ProcessBashCall(toolCall),
            _ => CreateErrorResult(toolCall, $"Error: unknown function '{toolCall.FunctionName}'. Available functions: {ToolHandler.ReadFunctionName}, {ToolHandler.WriteFunctionName}, {ToolHandler.BashFunctionName}.")
        };
    }

    private static ToolChatMessage CreateErrorResult(ChatToolCall toolCall, string errorMessage)
    {
        Console.Error.WriteLine($"[tool error] {errorMessage}");
        return new ToolChatMessage(toolCall.Id, errorMessage);
    }

    private static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Read tool called with no arguments. Expected format: {\"file_path\": \"<path>\"}");
        }

        ToolHandler.ReadFileCall? readFileCall;
        try
        {
            readFileCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.ReadFileCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Read tool arguments: {ex.Message}. Expected format: {{\"file_path\": \"<path>\"}}");
        }

        if (readFileCall?.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Read tool missing required parameter 'file_path'. Expected format: {\"file_path\": \"<path>\"}");
        }

        try
        {
            string fileText = System.IO.File.ReadAllText(readFileCall.file_path);
            int maxTokens = Configuration.GetMaxToolResultTokens();
            fileText = ContextManager.TruncateToolResult(fileText, maxTokens);
            return new ToolChatMessage(toolCall.Id, fileText);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error reading file '{readFileCall.file_path}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessWriteFileCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool called with no arguments. Expected format: {\"file_path\": \"<path>\", \"content\": \"<content>\"}");
        }

        ToolHandler.WriteFileCall? writeFileCall;
        try
        {
            writeFileCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.WriteFileCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Write tool arguments: {ex.Message}. Expected format: {{\"file_path\": \"<path>\", \"content\": \"<content>\"}}");
        }

        if (writeFileCall?.file_path == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'file_path'.");
        }

        if (writeFileCall?.content == null)
        {
            return CreateErrorResult(toolCall, "Error: Write tool missing required parameter 'content'.");
        }

        try
        {
            string? directory = System.IO.Path.GetDirectoryName(writeFileCall.file_path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            System.IO.File.WriteAllText(writeFileCall.file_path, writeFileCall.content);
            return new ToolChatMessage(toolCall.Id, $"Successfully wrote content to {writeFileCall.file_path}");
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error writing file '{writeFileCall.file_path}': {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessBashCall(ChatToolCall toolCall)
    {
        if (toolCall.FunctionArguments == null)
        {
            return CreateErrorResult(toolCall, "Error: Bash tool called with no arguments. Expected format: {\"command\": \"<command>\"}");
        }

        ToolHandler.BashCommandCall? bashCall;
        try
        {
            bashCall = toolCall.FunctionArguments.ToObjectFromJson<ToolHandler.BashCommandCall>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return CreateErrorResult(toolCall, $"Error: invalid JSON in Bash tool arguments: {ex.Message}. Expected format: {{\"command\": \"<command>\"}}");
        }

        if (bashCall?.command == null)
        {
            return CreateErrorResult(toolCall, "Error: Bash tool missing required parameter 'command'.");
        }

        try
        {
            bool isWindows = OperatingSystem.IsWindows();
            string shell = isWindows ? "powershell.exe" : "bash";
            string argumentsPrefix = isWindows ? "-Command" : "-c";

            var processStartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"{argumentsPrefix} \"{bashCall.command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            process.WaitForExit();

            string result;
            if (process.ExitCode == 0)
            {
                result = stdout;
            }
            else
            {
                result = $"Exit code: {process.ExitCode}\n\nstdout:\n{stdout}\n\nstderr:\n{stderr}";
            }

            int maxTokens = Configuration.GetMaxToolResultTokens();
            result = ContextManager.TruncateToolResult(result, maxTokens);

            return new ToolChatMessage(toolCall.Id, result);
        }
        catch (Exception ex)
        {
            return CreateErrorResult(toolCall, $"Error executing command '{bashCall.command}': {ex.Message}");
        }
    }

    public static void DisplayConsoleContent(ChatCompletion response)
    {
        if (response.Content == null || response.Content.Count == 0)
        {
            return;
        }

        for (int i = 0; i < response.Content.Count; i++)
        {
            var part = response.Content[i];
            if (!string.IsNullOrEmpty(part.Text))
            {
                Console.Write(part.Text);
            }
        }
    }
}
