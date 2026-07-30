namespace TerminalAiAssistant;

public static class ConsoleStyler
{
    public static ConsoleColorScope WithColor(ConsoleColor foreground) => new(foreground);

    public static void WriteLine(string text, ConsoleColor color, TextWriter? writer = null)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        (writer ?? Console.Out).WriteLine(text);
        Console.ForegroundColor = prev;
    }

    public static void Write(string text, ConsoleColor color, TextWriter? writer = null)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        (writer ?? Console.Out).Write(text);
        Console.ForegroundColor = prev;
    }
}

public readonly struct ConsoleColorScope : IDisposable
{
    private readonly ConsoleColor _previous;

    public ConsoleColorScope(ConsoleColor foreground)
    {
        _previous = Console.ForegroundColor;
        Console.ForegroundColor = foreground;
    }

    public void Dispose()
    {
        Console.ForegroundColor = _previous;
    }
}
