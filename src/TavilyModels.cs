namespace TerminalAiAssistant;

internal sealed class TavilyResponse
{
    public string? answer { get; init; }
    public List<TavilyResult>? results { get; init; }
}

internal sealed class TavilyResult
{
    public string? title { get; init; }
    public string? url { get; init; }
    public string? content { get; init; }
    public double? score { get; init; }
}
