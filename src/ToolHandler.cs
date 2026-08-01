using OpenAI.Chat;
using System.ClientModel;

namespace TerminalAiAssistant;

public static class ToolHandler
{
    public const string ReadFunctionName = "Read";
    public const string WriteFunctionName = "Write";
    public const string EditFunctionName = "Edit";
    public const string ApplyPatchFunctionName = "ApplyPatch";
    public const string DiffFunctionName = "Diff";
    public const string BashFunctionName = "Bash";
    public const string GlobFunctionName = "Glob";
    public const string GrepFunctionName = "Grep";
    public const string WebFetchFunctionName = "WebFetch";
    public const string WebSearchFunctionName = "WebSearch";
    public const string QuestionFunctionName = "Question";
    public const string TaskFunctionName = "Task";
    public const string TodoWriteFunctionName = "TodoWrite";

    private const string ReadFunctionDescription = "Read and return the contents of a file";
    private const string WriteFunctionDescription = "Write content to a file";
    private const string EditFunctionDescription = "Edit a file by performing a string replacement. Use this to make targeted changes instead of rewriting the entire file. Provide the old text to find and the new text to replace it with. The old text is matched with fuzzy tolerance for leading/trailing whitespace on each line and unicode differences, and as a last resort by line-sequence similarity, so it need not be byte-for-byte exact. Can span multiple lines for larger replacements. On success, the applied diff is returned.";
    private const string ApplyPatchFunctionDescription = "Apply a unified diff (patch) to a file, making multiple changes in one call. The patch must contain one or more hunks in the format '@@ -start,count +start,count @@' followed by lines prefixed with ' ' (context), '-' (removed), and '+' (added). File headers (--- / +++) are optional and must not include timestamps. Hunks are located by content with fuzzy tolerance for leading/trailing whitespace on each line, so context lines need not match byte-for-byte. Hunk header line counts must match the hunk body. The result reports how each hunk matched and returns the applied diff. Code fences around the patch are ignored. To create a new file, the file must not exist and the patch must contain only '+' lines. Use this instead of repeated Edit calls when changes span multiple hunks.";
    private const string DiffFunctionDescription = "Generate a preview of the changes that would be made to a file, WITHOUT writing anything. Compares the current contents of file_path on disk with new_content and returns a unified diff with @@ hunks. Use this to inspect a change before applying it with the ApplyPatch or Edit tools. Returns 'No differences' if the content is identical.";
    private const string BashFunctionDescription = "Execute a shell command";
    private const string GlobFunctionDescription = "Find files by glob pattern. Supports ** (any depth), * (wildcard), ? (single char), and {a,b} (alternation) patterns. Returns full paths of matching files, one per line.";
    private const string GrepFunctionDescription = "Search for patterns in files using ripgrep. Supports regex patterns. Returns matching lines with file paths and line numbers. Respects .gitignore by default, skips binary files.";
    private const string WebFetchFunctionDescription = "Fetch and return the contents of a URL. Converts HTML pages to markdown for readability. Use this to read documentation, check API responses, or fetch web content.";
    private const string WebSearchFunctionDescription = "Search the web for current information using Tavily search. Returns a list of results with titles, URLs, and content snippets. Use this when you need up-to-date information, documentation, or answers not available in the local codebase.";
    private const string QuestionFunctionDescription = "Ask the user a question with multiple-choice options. Use this when you are uncertain about an approach, need a decision, or the task is ambiguous. Present 2-6 clear options with short labels and descriptions. Do not guess when you can ask.";
    private const string TaskFunctionDescription = "Launch a sub-agent to handle a complex subtask. The sub-agent has access to all the same tools (Read, Write, Edit, ApplyPatch, Diff, Bash, Glob, Grep, WebFetch, WebSearch, Question, TodoWrite). Use this when a task is complex enough to warrant a dedicated sub-agent — it will run independently and return its result. The sub-agent cannot launch further sub-agents.";
    private const string TodoWriteFunctionDescription = "Create and maintain a structured task list for the current session. Tracks progress, organizes multi-step work. Call this at the START of complex multi-step tasks to plan, and UPDATE it as you complete steps. Each call replaces the entire list — pass ALL items including completed ones.";
    private const string FilePathPropertyName = "file_path";

    private const string PatternParameter = "pattern";
    private const string DescriptionParameter = "description";
    private const string ContentParameter = "content";

    public static ChatTool CreateReadTool() =>
        CreateTool(ReadFunctionName, ReadFunctionDescription, [FilePathPropertyName],
            StringProperties((FilePathPropertyName, "The path to the file to read")));

    public static ChatTool CreateWriteTool() =>
        CreateTool(WriteFunctionName, WriteFunctionDescription, [FilePathPropertyName, ContentParameter],
            StringProperties(
                (FilePathPropertyName, "The path of the file to write to"),
                (ContentParameter, "The content to write to the file")));

    public static ChatTool CreateEditTool() =>
        CreateTool(EditFunctionName, EditFunctionDescription, [FilePathPropertyName, "old_string", "new_string"],
            StringProperties(
                (FilePathPropertyName, "The path to the file to edit"),
                ("old_string", "The text to search for (matched with fuzzy tolerance for whitespace and unicode differences)"),
                ("new_string", "The text to replace it with")));

    public static ChatTool CreateApplyPatchTool() =>
        CreateTool(ApplyPatchFunctionName, ApplyPatchFunctionDescription, [FilePathPropertyName, "patch"],
            StringProperties(
                (FilePathPropertyName, "The path to the file to patch"),
                ("patch", "The unified diff to apply. One or more hunks in the format '@@ -start,count +start,count @@', followed by lines prefixed with ' ' (context), '-' (removed), and '+' (added). No timestamps after the --- / +++ headers. Include 2-5 context lines per hunk for unique matching.")));

    public static ChatTool CreateDiffTool() =>
        CreateTool(DiffFunctionName, DiffFunctionDescription, [FilePathPropertyName, "new_content"],
            StringProperties(
                (FilePathPropertyName, "The path of the file to compare against (read from disk, not modified)"),
                ("new_content", "The proposed new content to compare with the current file contents")));

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

    public static ChatTool CreateQuestionTool() =>
        CreateTool(QuestionFunctionName, QuestionFunctionDescription,
            ["question", "options"],
            new Dictionary<string, object>
            {
                ["question"] = StringProp("The question to ask the user"),
                ["header"] = StringProp("Very short label (max 30 chars)"),
                ["options"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    [DescriptionParameter] = "Available choices for the user (2-6 recommended)",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { "label" },
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["label"] = StringProp("Display text for the choice (1-5 words, concise)"),
                            [DescriptionParameter] = StringProp("Explanation of what this choice means")
                        }
                    }
                },
                ["allow_custom"] = StringProp("Set to 'true' to let the user type a custom answer (default: 'false')")
            });

    public static ChatTool CreateTaskTool() =>
        CreateTool(TaskFunctionName, TaskFunctionDescription, [DescriptionParameter],
            StringProperties(
                (DescriptionParameter, "The task description for the sub-agent. Be clear, specific, and include all necessary context from the current session."),
                ("subagent_type", "Optional sub-agent type (default: 'general'). Reserved for future use.")));

    public static ChatTool CreateTodoWriteTool() =>
        CreateTool(TodoWriteFunctionName, TodoWriteFunctionDescription, ["todos"],
            new Dictionary<string, object>
            {
                ["todos"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    [DescriptionParameter] = "Task list items",
                    ["items"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["required"] = new[] { ContentParameter },
                        ["properties"] = new Dictionary<string, object>
                        {
                            [ContentParameter] = StringProp("Brief description of the task"),
                            ["status"] = StringProp("Status: 'pending', 'in_progress', 'completed', or 'cancelled' (default: 'pending')"),
                            ["priority"] = StringProp("Priority: 'high', 'medium', or 'low' (default: 'medium')")
                        }
                    }
                }
            });

    public static ChatCompletionOptions CreateCompletionOptions()
    {
        return new ChatCompletionOptions
        {
            Tools =
            {
                CreateReadTool(),
                CreateWriteTool(),
                CreateEditTool(),
                CreateApplyPatchTool(),
                CreateDiffTool(),
                CreateBashTool(),
                CreateGlobTool(),
                CreateGrepTool(),
                CreateWebFetchTool(),
                CreateWebSearchTool(),
                CreateQuestionTool(),
                CreateTaskTool(),
                CreateTodoWriteTool()
            }
        };
    }

    public static ChatCompletionOptions CreateSubAgentCompletionOptions()
    {
        return new ChatCompletionOptions
        {
            Tools =
            {
                CreateReadTool(),
                CreateWriteTool(),
                CreateEditTool(),
                CreateApplyPatchTool(),
                CreateDiffTool(),
                CreateBashTool(),
                CreateGlobTool(),
                CreateGrepTool(),
                CreateWebFetchTool(),
                CreateWebSearchTool(),
                CreateQuestionTool(),
                CreateTodoWriteTool()
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
            [DescriptionParameter] = p.description
        });

    private static Dictionary<string, object> StringProp(string description) =>
        new()
        {
            ["type"] = "string",
            [DescriptionParameter] = description
        };

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

    public class ApplyPatchCall
    {
        public required string file_path { get; set; }
        public required string patch { get; set; }
    }

    public class DiffCall
    {
        public required string file_path { get; set; }
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

    public class QuestionCall
    {
        public required string question { get; set; }
        public string? header { get; set; }
        public required List<QuestionOption> options { get; set; }
        public string? allow_custom { get; set; }
    }

    public class QuestionOption
    {
        public required string label { get; set; }
        public string? description { get; set; }
    }

    public class TaskCall
    {
        public required string description { get; set; }
        public string? subagent_type { get; set; }
    }

    public class TodoWriteCall
    {
        public required List<TodoItem> todos { get; set; }
    }
}