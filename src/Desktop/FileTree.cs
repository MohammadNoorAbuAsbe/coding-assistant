using System.Text.Json;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Builds the lazy file tree shown in the sidebar. Only the root level is
/// scanned initially; directories expand on demand via <c>files:expand</c>.
/// </summary>
internal static class FileTree
{
    private static readonly string[] IgnoredDirectories =
    {
        ".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj",
        "dist", "build", "out", "artifacts", "packages", "TestResults",
        "__pycache__", ".venv", "venv", ".pytest_cache", ".ruff_cache",
        ".mypy_cache", ".terraform", ".next", ".nuxt", ".cache", "coverage",
        ".gradle", ".hg", ".svn", ".bzr", "target", "vendor", "tmp"
    };

    private static readonly string[] IgnoredFiles =
    {
        ".DS_Store", "Thumbs.db", "desktop.ini"
    };

    private const int MaxRootEntries = 400;

    public static List<TreeNode> BuildRoot(string directory)
    {
        try
        {
            var entries = new DirectoryInfo(directory).GetFileSystemInfos()
                .Where(ShouldInclude)
                .OrderByDescending(i => (i.Attributes & FileAttributes.Directory) != 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRootEntries)
                .ToList();

            return entries.Select(i => CreateNode(i, isRoot: true)).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static List<TreeNode> BuildChildren(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).GetFileSystemInfos()
                .Where(ShouldInclude)
                .OrderByDescending(i => (i.Attributes & FileAttributes.Directory) != 0)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(i => CreateNode(i, isRoot: false))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool ShouldInclude(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.Hidden) != 0)
            return false;

        if ((info.Attributes & FileAttributes.Directory) != 0)
            return !IgnoredDirectories.Contains(info.Name, StringComparer.OrdinalIgnoreCase);

        return !IgnoredFiles.Contains(info.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static TreeNode CreateNode(FileSystemInfo info, bool isRoot)
    {
        bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
        return new TreeNode
        {
            name = info.Name,
            path = info.FullName,
            kind = isDir ? "dir" : "file",
            children = null
        };
    }

    public static JsonDocument? ReadFile(string fullPath, int maxBytes)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
                return JsonDocument.Parse(JsonSerializer.Serialize(new { error = "File not found." }));

            bool truncated = info.Length > maxBytes;
            string content;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var buffer = new char[Math.Min(maxBytes, 262144)];
                int read = reader.Read(buffer, 0, buffer.Length);
                content = new string(buffer, 0, read);
            }

            return JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                path = fullPath,
                content,
                truncated,
                readBytes = content.Length
            }));
        }
        catch (Exception ex)
        {
            return JsonDocument.Parse(JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }
}

internal sealed class TreeNode
{
    public string name { get; set; } = "";
    public string path { get; set; } = "";
    public string kind { get; set; } = "";
    public List<TreeNode>? children { get; set; }
}
