using OpenAI.Chat;
using TerminalAiAssistant;

namespace TerminalAiAssistant.Tests;

public sealed class TempWorkspace : IDisposable
{
    public string Root { get; }
    private readonly string _originalCwd;
    private readonly Dictionary<string, string?> _savedEnv = new();
    private bool _disposed;

    public TempWorkspace()
    {
        _originalCwd = Environment.CurrentDirectory;
        Root = Path.Combine(Path.GetTempPath(), "taa-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Environment.CurrentDirectory = Root;
    }

    public void SaveEnv(string name)
    {
        if (!_savedEnv.ContainsKey(name))
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
        }
    }

    public void RestoreAllEnv()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
        _savedEnv.Clear();
    }

    public string WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(Root, relativePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public string ReadFile(string relativePath) => File.ReadAllText(Path.Combine(Root, relativePath));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreAllEnv();
        Environment.CurrentDirectory = _originalCwd;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best effort cleanup; ignored on failure.
        }
    }
}

public static class ToolCallFactory
{
    public static ChatToolCall Create(string name, string jsonArgs)
        => ChatToolCall.CreateFunctionToolCall("test-call-id", name, BinaryData.FromString(jsonArgs));
}
