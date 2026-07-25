using System.Diagnostics;
using OpenAI.Chat;

namespace TerminalAiAssistant;

public static class ResponseHandler
{
    public static List<ChatMessage> ProcessToolCalls(ChatCompletion response)
    {
        var toolResultMessages = new List<ChatMessage>();

        if (response.ToolCalls == null || response.ToolCalls.Count == 0)
        {
            return toolResultMessages;
        }

        foreach (var toolCall in response.ToolCalls)
        {
            if (toolCall.FunctionName == ToolHandler.ReadFunctionName)
            {
                var result = ProcessReadFileCall(toolCall);
                if (result != null)
                {
                    toolResultMessages.Add(result);
                }
            }
            else if (toolCall.FunctionName == ToolHandler.WriteFunctionName)
            {
                var result = ProcessWriteFileCall(toolCall);
                if (result != null)
                {
                    toolResultMessages.Add(result);
                }
            }
            else if (toolCall.FunctionName == ToolHandler.BashFunctionName)
            {
                var result = ProcessBashCall(toolCall);
                if (result != null)
                {
                    toolResultMessages.Add(result);
                }
            }
        }

        return toolResultMessages;
    }

    private static ToolChatMessage? ProcessReadFileCall(ChatToolCall toolCall)
    {
        BinaryData? functionArguments = toolCall.FunctionArguments;
        if (functionArguments == null)
        {
            return null;
        }

        ToolHandler.ReadFileCall? readFileCall = functionArguments.ToObjectFromJson<ToolHandler.ReadFileCall>();
        if (readFileCall?.file_path == null)
        {
            return null;
        }

        try
        {
            string fileText = System.IO.File.ReadAllText(readFileCall.file_path);
            return new ToolChatMessage(toolCall.Id, fileText);
        }
        catch (Exception ex)
        {
            return new ToolChatMessage(toolCall.Id, $"Error reading file: {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessWriteFileCall(ChatToolCall toolCall)
    {
        BinaryData? functionArguments = toolCall.FunctionArguments;
        if (functionArguments == null)
        {
            return null;
        }

        ToolHandler.WriteFileCall? writeFileCall = functionArguments.ToObjectFromJson<ToolHandler.WriteFileCall>();
        if (writeFileCall?.file_path == null || writeFileCall?.content == null)
        {
            return null;
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
            return new ToolChatMessage(toolCall.Id, $"Error writing file: {ex.Message}");
        }
    }

    private static ToolChatMessage? ProcessBashCall(ChatToolCall toolCall)
    {
        BinaryData? functionArguments = toolCall.FunctionArguments;
        if (functionArguments == null)
        {
            return null;
        }

        ToolHandler.BashCommandCall? bashCall = functionArguments.ToObjectFromJson<ToolHandler.BashCommandCall>();
        if (bashCall?.command == null)
        {
            return null;
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

            return new ToolChatMessage(toolCall.Id, result);
        }
        catch (Exception ex)
        {
            return new ToolChatMessage(toolCall.Id, $"Error executing command: {ex.Message}");
        }
    }

    public static void DisplayConsoleContent(ChatCompletion response)
    {
        if (response.Content == null || response.Content.Count == 0)
        {
            return;
        }

        Console.Write(response.Content[0].Text);
    }
}
