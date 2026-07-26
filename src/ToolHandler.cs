using OpenAI.Chat;
using System.ClientModel;

namespace TerminalAiAssistant;

public static class ToolHandler
{
    public const string ReadFunctionName = "Read";
    public const string WriteFunctionName = "Write";
    public const string EditFunctionName = "Edit";
    public const string BashFunctionName = "Bash";
    public const string GrepFunctionName = "Grep";

    private const string ReadFunctionDescription = "Read and return the contents of a file";
    private const string WriteFunctionDescription = "Write content to a file";
    private const string EditFunctionDescription = "Edit a file by performing an exact string replacement. Use this to make targeted changes instead of rewriting the entire file. Provide the exact old string to find and the new string to replace it with.";
    private const string BashFunctionDescription = "Execute a shell command";
    private const string GrepFunctionDescription = "Search for patterns in files using ripgrep. Supports regex patterns. Returns matching lines with file paths and line numbers. Respects .gitignore by default, skips binary files.";
    private const string FilePathPropertyName = "file_path";


    public static ChatTool CreateReadTool() =>
        CreateTool(ReadFunctionName, ReadFunctionDescription, [FilePathPropertyName],
            StringProperties((FilePathPropertyName, "The path to the file to read")));

    public static ChatTool CreateWriteTool() =>
        CreateTool(WriteFunctionName, WriteFunctionDescription, [FilePathPropertyName, "content"],
            StringProperties(
                (FilePathPropertyName, "The path of the file to write to"),
                ("content", "The content to write to the file")));

    public static ChatTool CreateEditTool() =>
        CreateTool(EditFunctionName, EditFunctionDescription, [FilePathPropertyName, "old_string", "new_string"],
            StringProperties(
                (FilePathPropertyName, "The path to the file to edit"),
                ("old_string", "The exact text to search for (must match exactly, including whitespace)"),
                ("new_string", "The text to replace it with")));

    public static ChatTool CreateBashTool() =>
        CreateTool(BashFunctionName, BashFunctionDescription, ["command"],
            StringProperties(("command", "The command to execute")));

    public static ChatTool CreateGrepTool() =>
        CreateTool(GrepFunctionName, GrepFunctionDescription, ["pattern"],
            StringProperties(
                ("pattern", "Regex pattern to search for (e.g., 'TODO', 'function\\s+\\w+', '\\.cs$')"),
                ("path", "Directory or file path to search (defaults to current directory)"),
                ("include", "File glob pattern to include (e.g., '*.cs', '*.py', '*.js')"),
                ("exclude", "Glob pattern to exclude (e.g., 'node_modules/**', '*.min.js')"),
                ("case_insensitive", "Set to 'true' for case-insensitive search (default: false)"),
                ("context_lines", "Number of context lines before and after each match (default: 0)")));

    public static ChatCompletionOptions CreateCompletionOptions()
    {
        return new ChatCompletionOptions
        {
            Tools =
            {
                CreateReadTool(),
                CreateWriteTool(),
                CreateEditTool(),
                CreateBashTool(),
                CreateGrepTool()
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

    public class EditFileCall
    {
        public required string file_path { get; set; }
        public required string old_string { get; set; }
        public required string new_string { get; set; }
    }

    public class BashCommandCall
    {
        public required string command { get; set; }
    }

    public class GrepCall
    {
        public required string pattern { get; set; }
        public string? path { get; set; }
        public string? include { get; set; }
        public string? exclude { get; set; }
        public string? case_insensitive { get; set; }
        public string? context_lines { get; set; }
    }
}