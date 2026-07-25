using OpenAI.Chat;
using System.ClientModel;

namespace TerminalAiAssistant;

public static class ToolHandler
{
    public const string ReadFunctionName = "Read";
    public const string WriteFunctionName = "Write";
    public const string BashFunctionName = "Bash";

    private const string ReadFunctionDescription = "Read and return the contents of a file";
    private const string WriteFunctionDescription = "Write content to a file";
    private const string BashFunctionDescription = "Execute a shell command";


    public static ChatTool CreateReadTool()
    {
        return ChatTool.CreateFunctionTool(
            ReadFunctionName,
            ReadFunctionDescription,
            CreateFunctionParameters(["file_path"], new
            {
                file_path = new
                {
                    type = "string",
                    description = "The path to the file to read"
                }
            }));
    }

    public static ChatTool CreateWriteTool()
    {
        return ChatTool.CreateFunctionTool(
            WriteFunctionName,
            WriteFunctionDescription,
            CreateFunctionParameters(["file_path", "content"], new
            {
                file_path = new
                {
                    type = "string",
                    description = "The path of the file to write to"
                },
                content = new
                {
                    type = "string",
                    description = "The content to write to the file"
                }
            }));
    }

    public static ChatTool CreateBashTool()
    {
        return ChatTool.CreateFunctionTool(
            BashFunctionName,
            BashFunctionDescription,
            CreateFunctionParameters(["command"], new
            {
                command = new
                {
                    type = "string",
                    description = "The command to execute"
                }
            }));
    }

    public static ChatCompletionOptions CreateCompletionOptions()
    {
        return new ChatCompletionOptions
        {
            Tools =
            {
                CreateReadTool(),
                CreateWriteTool(),
                CreateBashTool()
            }
        };
    }

    private static BinaryData CreateFunctionParameters(string[] required, object properties)
    {
        return BinaryData.FromObjectAsJson(new
        {
            type = "object",
            required,
            properties
        });
    }

    public class ReadFileCall
    {
        public required string file_path { get; set; }
    }

    public class WriteFileCall
    {
        public required string file_path { get; set; }
        public required string content { get; set; }
    }

    public class BashCommandCall
    {
        public required string command { get; set; }
    }
}