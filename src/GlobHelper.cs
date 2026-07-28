using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using System.Text;

namespace TerminalAiAssistant;

internal static class GlobHelper
{
    private const int MaxResults = 100;

    internal static string FindFiles(string pattern, string? path)
    {
        string rootDir = path ?? Environment.CurrentDirectory;

        if (!Directory.Exists(rootDir))
        {
            return $"Error: directory '{rootDir}' does not exist";
        }

        var matcher = new Matcher();
        matcher.AddInclude(pattern);

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(rootDir)));

        if (!result.HasMatches)
        {
            return $"No files matching pattern '{pattern}' found in '{rootDir}'";
        }

        int totalMatches = result.Files.Count();
        var files = result.Files
            .Select(f => Path.GetFullPath(Path.Combine(rootDir, f.Path)))
            .Take(MaxResults)
            .ToList();

        var sb = new StringBuilder();
        for (int i = 0; i < files.Count; i++)
        {
            sb.AppendLine(files[i]);
        }

        if (totalMatches > MaxResults)
        {
            sb.AppendLine($"[showing {MaxResults} of {totalMatches} matches, refine your pattern to narrow results]");
        }

        return sb.ToString().TrimEnd();
    }
}
