namespace TerminalAiAssistant;

public static class ConsoleStyler
{
    internal static readonly bool NoColor = Environment.GetEnvironmentVariable("NO_COLOR") != null;

    public static ConsoleColorScope WithColor(ConsoleColor foreground) => new(foreground);

    public static void WriteLine(string text, ConsoleColor color, TextWriter? writer = null)
    {
        if (NoColor) { (writer ?? Console.Out).WriteLine(text); return; }
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        (writer ?? Console.Out).WriteLine(text);
        Console.ForegroundColor = prev;
    }

    public static void Write(string text, ConsoleColor color, TextWriter? writer = null)
    {
        if (NoColor) { (writer ?? Console.Out).Write(text); return; }
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        (writer ?? Console.Out).Write(text);
        Console.ForegroundColor = prev;
    }
}

public readonly struct ConsoleColorScope : IDisposable
{
    private readonly ConsoleColor _previous;
    private readonly bool _active;

    public ConsoleColorScope(ConsoleColor foreground)
    {
        _active = !ConsoleStyler.NoColor;
        if (_active)
        {
            _previous = Console.ForegroundColor;
            Console.ForegroundColor = foreground;
        }
        else
        {
            _previous = default;
        }
    }

    public void Dispose()
    {
        if (_active)
            Console.ForegroundColor = _previous;
    }
}
