using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace TerminalAiAssistant;

/// <summary>
/// Taps the raw SSE response stream of chat completions and extracts the model's
/// reasoning content (chain of thought), which the OpenAI SDK silently drops.
/// Recognizes the field names used by different providers: "reasoning_content"
/// (OpenAI o-series, DeepSeek, Ollama), "reasoning" (Qwen3, Gemini via OpenRouter),
/// and "thinking" (Gemini). Set HIDE_REASONING=1 to disable.
/// </summary>
internal sealed class ReasoningTapPolicy : PipelinePolicy
{
    internal static readonly ConcurrentQueue<string> Pending = new();

    internal static bool Enabled => Environment.GetEnvironmentVariable("HIDE_REASONING") != "1";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        TapResponse(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex);
        TapResponse(message);
    }

    private static void TapResponse(PipelineMessage message)
    {
        if (!Enabled)
            return;

        if (message.Response?.ContentStream is not { } original)
            return;

        if (!message.Response.Headers.TryGetValue("Content-Type", out string? contentType) ||
            contentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        message.Response.ContentStream = new SseReasoningTeeStream(original);
    }
}

/// <summary>
/// Pass-through stream that observes SSE events and pushes reasoning fragments
/// into <see cref="ReasoningTapPolicy.Pending"/> without altering the byte stream.
/// </summary>
internal sealed class SseReasoningTeeStream : Stream
{
    private static readonly string[] ReasoningKeys = ["reasoning_content", "reasoning", "thinking"];

    private readonly Stream _inner;
    private byte[] _buffer = new byte[8192];
    private int _length;

    public SseReasoningTeeStream(Stream inner) => _inner = inner;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        if (n > 0)
            Feed(buffer.AsSpan(offset, n));
        return n;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = _inner.Read(buffer);
        if (n > 0)
            Feed(buffer[..n]);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await _inner.ReadAsync(buffer, cancellationToken);
        if (n > 0)
            Feed(buffer.Span[..n]);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        if (n > 0)
            Feed(buffer.AsSpan(offset, n));
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
        await base.DisposeAsync();
    }

    private void Feed(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));

        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
        ProcessEvents();
    }

    private void ProcessEvents()
    {
        int consumed = 0;
        int searchFrom = 0;

        while (true)
        {
            int end = FindEventEnd(searchFrom);
            if (end < 0)
                break;

            string eventText = Encoding.UTF8.GetString(_buffer, searchFrom, end - searchFrom);
            HandleEvent(eventText);
            
            // The event ends with a double-newline. We must consume both.
            // If it was \n\n, that's 2 bytes. If it was \r\n\r\n, that's 4 bytes.
            int separatorLength = (_buffer[end] == '\r') ? 4 : 2;
            consumed = end + separatorLength;
            searchFrom = consumed;
        }

        if (consumed > 0)
        {
            int remaining = _length - consumed;
            Array.Copy(_buffer, consumed, _buffer, 0, remaining);
            _length = remaining;
        }
    }

    /// <summary>
    /// Finds the end of the SSE event starting at <paramref name="start"/>: either
    /// "\n\n" (LF) or "\r\n\r\n" (CRLF). Returns the index of the first separator byte.
    /// </summary>
    private int FindEventEnd(int start)
    {
        for (int i = start; i + 1 < _length; i++)
        {
            if (_buffer[i] == '\n' && _buffer[i + 1] == '\n')
                return i;
            if (_buffer[i] == '\r' && i + 3 < _length &&
                _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static void HandleEvent(string eventText)
    {
        foreach (var rawLine in eventText.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            string payload = line[5..].TrimStart();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;

            ExtractReasoning(payload);
        }
    }

    private static void ExtractReasoning(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);

            if (!doc.RootElement.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return;
            }

            JsonElement delta = choices[0].TryGetProperty("delta", out JsonElement d) ? d : default;
            if (delta.ValueKind != JsonValueKind.Object)
                return;

            foreach (string key in ReasoningKeys)
            {
                if (delta.TryGetProperty(key, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    string? text = value.GetString();
                    if (!string.IsNullOrEmpty(text))
                        ReasoningTapPolicy.Pending.Enqueue(text);
                }
            }
        }
        catch (JsonException)
        {
        }
    }
}
