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
    private const string FilePathPropertyName = "file_path";


    public static ChatTool CreateReadTool() =>
        CreateTool(ReadFunctionName, ReadFunctionDescription, [FilePathPropertyName],
            StringProperties((FilePathPropertyName, "The path to the file to read")));

    public static ChatTool CreateWriteTool() =>
        CreateTool(WriteFunctionName, WriteFunctionDescription, [FilePathPropertyName, "content"],
            StringProperties(
                (FilePathPropertyName, "The path of the file to write to"),
                ("content", "The content to write to the file")));

    public static ChatTool CreateBashTool() =>
        CreateTool(BashFunctionName, BashFunctionDescription, ["command"],
            StringProperties(("command", "The command to execute")));

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

    private static ChatTool CreateTool(string name, string description, string[] required, object properties) =>
        ChatTool.CreateFunctionTool(name, description, CreateFunctionParameters(required, properties));

    private static BinaryData CreateFunctionParameters(string[] required, object properties)
    {
        return BinaryData.FromObjectAsJson(new
        {
            type = "object",
            required,
            properties
        });
    }

    private static Dictionary<string, object> StringProperties(params (string name, string description)[] props) =>
        props.ToDictionary(p => p.name, p => (object)new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = p.description
        });

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