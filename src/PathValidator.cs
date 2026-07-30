using System;
using System.IO;
using System.Linq;

namespace TerminalAiAssistant;

internal class PathOutsideWorkspaceException : InvalidOperationException
{
    public PathOutsideWorkspaceException(string path)
        : base($"Access denied: path '{path}' is outside the workspace.")
    {
    }
}

internal static class PathValidator
{
    private static readonly string[] WindowsDeviceNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    internal static string ValidatePath(string userPath, string workspaceRoot)
    {
        if (string.IsNullOrEmpty(userPath))
            throw new ArgumentException("Path cannot be null or empty.", nameof(userPath));

        if (string.IsNullOrEmpty(workspaceRoot))
            throw new ArgumentException("Workspace root cannot be null or empty.", nameof(workspaceRoot));

        if (OperatingSystem.IsWindows())
        {
            if (userPath.StartsWith(@"\\.\") || userPath.StartsWith(@"\\?\"))
                throw new PathOutsideWorkspaceException(userPath);

            string fileName = Path.GetFileName(userPath);
            if (WindowsDeviceNames.Any(n => string.Equals(n, fileName, StringComparison.OrdinalIgnoreCase)))
                throw new PathOutsideWorkspaceException(userPath);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(userPath);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is System.Security.SecurityException || ex is NotSupportedException || ex is PathTooLongException)
        {
            throw new PathOutsideWorkspaceException(userPath);
        }

        string fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        if (!fullWorkspaceRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
            fullWorkspaceRoot += Path.DirectorySeparatorChar;

        if (!fullPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
            fullPath += Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(fullWorkspaceRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new PathOutsideWorkspaceException(userPath);

        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }
}
