using System.Diagnostics;

namespace TerminalAiAssistant;

internal static class RipgrepHelper
{
    internal static string? FindRipgrep()
    {
        bool isWindows = OperatingSystem.IsWindows();
        string executableName = isWindows ? "rg.exe" : "rg";

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                string fullPath = Path.Combine(dir, executableName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        if (isWindows)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] knownPaths =
            [
                Path.Combine(localAppData, @"Microsoft\WinGet\Packages"),
                Path.Combine(localAppData, @"Programs\ripgrep"),
                Path.Combine(programFiles, "ripgrep"),
                Path.Combine(programFilesX86, "ripgrep")
            ];

            return knownPaths
                .Where(Directory.Exists)
                .Select(basePath => FindFileInDirectory(basePath, executableName))
                .FirstOrDefault(found => found != null);
        }

        return null;
    }

    private static string? FindFileInDirectory(string basePath, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(basePath, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static List<string> BuildRipgrepArguments(ToolHandler.GrepCall grepCall, string searchPath)
    {
        var args = new List<string>();

        args.Add("--max-count");
        args.Add("50");
        args.Add("--max-columns");
        args.Add("200");
        args.Add("--max-columns-preview");
        args.Add("-n");

        if (string.Equals(grepCall.case_insensitive, "true", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-i");
        }

        if (!string.IsNullOrEmpty(grepCall.context_lines) && int.TryParse(grepCall.context_lines, out int ctx) && ctx > 0)
        {
            args.Add("-C");
            args.Add(ctx.ToString());
        }

        if (!string.IsNullOrEmpty(grepCall.exclude))
        {
            args.Add("--glob");
            args.Add($"!{grepCall.exclude}");
        }

        if (!string.IsNullOrEmpty(grepCall.include))
        {
            args.Add("--glob");
            args.Add(grepCall.include);
        }

        args.Add(grepCall.pattern);
        args.Add(searchPath);

        return args;
    }
}