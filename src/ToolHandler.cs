using OpenAI.Chat;
using System.ClientModel;

namespace TerminalAiAssistant;

public static class ToolHandler
{
    public const string ReadFunctionName = "Read";
    public const string WriteFunctionName = "Write";
    public const string EditFunctionName = "Edit";
    public const string EditLineFunctionName = "EditLine";
    public const string BashFunctionName = "Bash";
    public const string GlobFunctionName = "Glob";
    public const string GrepFunctionName = "Grep";
    public const string WebFetchFunctionName = "WebFetch";
    public const string WebSearchFunctionName = "WebSearch";

    private const string ReadFunctionDescription = "Read and return the contents of a file";
    private const string WriteFunctionDescription = "Write content to a file";
    private const string EditFunctionDescription = "Edit a file by performing an exact string replacement. Use this to make targeted changes instead of rewriting the entire file. Provide the exact old string to find and the new string to replace it with.";
    private const string EditLineFunctionDescription = "Edit a file by replacing lines by line number. Use the Read tool first to see line numbers. Replace lines start_line through end_line (inclusive) with new_content. Use this when you cannot reproduce exact text for the Edit tool.";
    private const string BashFunctionDescription = "Execute a shell command";
    private const string GlobFunctionDescription = "Find files by glob pattern. Supports ** (any depth), * (wildcard), ? (single char), and {a,b} (alternation) patterns. Returns full paths of matching files, one per line.";
    private const string GrepFunctionDescription = "Search for patterns in files using ripgrep. Supports regex patterns. Returns matching lines with file paths and line numbers. Respects .gitignore by default, skips binary files.";
    private const string WebFetchFunctionDescription = "Fetch and return the contents of a URL. Converts HTML pages to markdown for readability. Use this to read documentation, check API responses, or fetch web content.";
    private const string WebSearchFunctionDescription = "Search the web for current information using Tavily search. Returns a list of results with titles, URLs, and content snippets. Use this when you need up-to-date information, documentation, or answers not available in the local codebase.";
    private const string FilePathPropertyName = "file_path";

    private const string PatternParameter = "pattern";
    
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

    public static ChatTool CreateEditLineTool() =>
        CreateTool(EditLineFunctionName, EditLineFunctionDescription, [FilePathPropertyName, "start_line", "end_line", "new_content"],
            StringProperties(
                (FilePathPropertyName, "The path to the file to edit"),
                ("start_line", "First line number to replace (1-indexed, inclusive)"),
                ("end_line", "Last line number to replace (1-indexed, inclusive)"),
                ("new_content", "The new content to replace the lines with")));

    public static ChatTool CreateBashTool() =>
        CreateTool(BashFunctionName, BashFunctionDescription, ["command"],
            StringProperties(("command", "The command to execute")));

    public static ChatTool CreateGlobTool() =>
        CreateTool(GlobFunctionName, GlobFunctionDescription, [PatternParameter],
            StringProperties(
                (PatternParameter, "Glob pattern to search for (e.g., '**/*.cs', 'src/**/*.ts', '*.json'). Supports ** (any directory depth), * (wildcard), ? (single character), and {a,b} (alternation)."),
                ("path", "Root directory to search in (defaults to current working directory)")));

    public static ChatTool CreateGrepTool() =>
        CreateTool(GrepFunctionName, GrepFunctionDescription, [PatternParameter],
            StringProperties(
                (PatternParameter, "Regex pattern to search for (e.g., 'TODO', 'function\\s+\\w+', '\\.cs$')"),
                ("path", "Directory or file path to search (defaults to current directory)"),
                ("include", "File glob pattern to include (e.g., '*.cs', '*.py', '*.js')"),
                ("exclude", "Glob pattern to exclude (e.g., 'node_modules/**', '*.min.js')"),
                ("case_insensitive", "Set to 'true' for case-insensitive search (default: false)"),
                ("context_lines", "Number of context lines before and after each match (default: 0)")));

    public static ChatTool CreateWebFetchTool() =>
        CreateTool(WebFetchFunctionName, WebFetchFunctionDescription, ["url"],
            StringProperties(
                ("url", "The URL to fetch content from"),
                ("format", "Response format: 'markdown' (default, converts HTML to markdown), 'text' (plain text), or 'html' (raw HTML). Default: markdown")));

    public static ChatTool CreateWebSearchTool() =>
        CreateTool(WebSearchFunctionName, WebSearchFunctionDescription, ["query"],
            StringProperties(
                ("query", "The search query"),
                ("max_results", "Maximum number of results to return (1-10, default: 5)"),
                ("search_depth", "Search depth: 'basic' (faster, cheaper) or 'advanced' (more thorough). Default: basic")));

    public static ChatCompletionOptions CreateCompletionOptions()
    {
        return new ChatCompletionOptions
        {
            Tools =
            {
                CreateReadTool(),
                CreateWriteTool(),
                CreateEditTool(),
                CreateEditLineTool(),
                CreateBashTool(),
                CreateGlobTool(),
                CreateGrepTool(),
                CreateWebFetchTool(),
                CreateWebSearchTool()
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

    public class EditLineCall
    {
        public required string file_path { get; set; }
        public required string start_line { get; set; }
        public required string end_line { get; set; }
        public required string new_content { get; set; }
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

    public class GlobCall
    {
        public required string pattern { get; set; }
        public string? path { get; set; }
    }

    public class WebFetchCall
    {
        public required string url { get; set; }
        public string? format { get; set; }
    }

    public class WebSearchCall
    {
        public required string query { get; set; }
        public string? max_results { get; set; }
        public string? search_depth { get; set; }
    }
}