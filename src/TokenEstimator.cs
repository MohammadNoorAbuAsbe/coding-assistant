using Microsoft.ML.Tokenizers;

namespace TerminalAiAssistant;

/// <summary>
/// Provider-aware token estimation. OpenAI-family models are counted exactly
/// with tiktoken (o200k_base for modern families, cl100k_base otherwise).
/// Every other model family (Gemini, Claude, Llama, Qwen, Mistral, ...) has no
/// tiktoken encoding, so a deterministic character heuristic is used: one token
/// per CJK character, and roughly one token per four other characters. The
/// residual error is self-correcting at runtime via ContextUsageTracker, which
/// calibrates the truncation budget against the API-reported usage.
/// </summary>
public static class TokenEstimator
{
    private static TiktokenTokenizer? _openAiTokenizer;
    private static readonly object Lock = new();

    private static bool UsesTiktoken()
    {
        var model = Configuration.GetModel()?.ToLowerInvariant() ?? "";
        return model.Contains("gpt", StringComparison.Ordinal)
            || model.Contains("o1", StringComparison.Ordinal)
            || model.Contains("o3", StringComparison.Ordinal)
            || model.Contains("o4", StringComparison.Ordinal)
            || model.Contains("chatgpt", StringComparison.Ordinal)
            || model.Contains("gpt-oss", StringComparison.Ordinal)
            || model.Contains("deepseek", StringComparison.Ordinal);
    }

    private static TiktokenTokenizer GetTiktokenTokenizer()
    {
        if (_openAiTokenizer != null) return _openAiTokenizer;

        lock (Lock)
        {
            if (_openAiTokenizer != null) return _openAiTokenizer;

            var model = Configuration.GetModel()?.ToLowerInvariant() ?? "";
            var encoding = model switch
            {
                string m when m.Contains("gpt-4o") || m.Contains("gpt-5") || m.Contains("gpt-oss")
                    || m.Contains("o1") || m.Contains("o3") || m.Contains("o4") || m.Contains("chatgpt") => "o200k_base",
                _ => "cl100k_base"
            };
            _openAiTokenizer = TiktokenTokenizer.CreateForEncoding(encoding);
            return _openAiTokenizer;
        }
    }

    /// <summary>
    /// Estimates the token count of <paramref name="text"/> using the
    /// encoding appropriate for the configured model.
    /// </summary>
    public static int Estimate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        if (UsesTiktoken())
        {
            return GetTiktokenTokenizer().CountTokens(text, considerPreTokenization: false, considerNormalization: false);
        }

        return EstimateHeuristic(text);
    }

    /// <summary>
    /// Deterministic heuristic count: CJK characters are roughly one token
    /// each; all other characters are roughly four per token. Never returns
    /// zero for non-empty input.
    /// </summary>
    public static int EstimateHeuristic(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // Count CJK characters once
        int cjkLength = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (IsCjk(c)) cjkLength++;
        }

        // Calculate tokens for non-CJK characters in bulk
        int otherLength = text.Length - cjkLength;
        int otherTokens = (otherLength + 3) / 4;  // Equivalent to ceil(otherLength / 4.0)
        return cjkLength + otherTokens;
    }

    private static bool IsCjk(char c)
    {
        return c >= 0x1100 && c <= 0x11FF        // Hangul Jamo
            || c >= 0x2E80 && c <= 0x2EFF        // CJK Radicals Supplement
            || c >= 0x3000 && c <= 0x30FF        // CJK Punctuation + Kana
            || c >= 0x3400 && c <= 0x4DBF        // CJK Extension A
            || c >= 0x4E00 && c <= 0x9FFF        // CJK Unified
            || c >= 0xAC00 && c <= 0xD7AF        // Hangul Syllables
            || c >= 0xF900 && c <= 0xFAFF        // CJK Compatibility
            || c >= 0x20000 && c <= 0x2FFFF;     // CJK Extension B+
    }

    /// <summary>
    /// Returns the character index in <paramref name="text"/> where the token
    /// count first exceeds <paramref name="maxTokens"/>, or the full length if
    /// the text fits within the budget. Exact for tiktoken-encoded models,
    /// approximate (single pass, per-character cost) for the heuristic path.
    /// </summary>
    public static int GetIndexByTokenCount(string text, int maxTokens)
    {
        if (string.IsNullOrEmpty(text) || maxTokens <= 0) return 0;

        if (UsesTiktoken())
        {
            return GetTiktokenTokenizer().GetIndexByTokenCount(text, maxTokens, out _, out _,
                considerPreTokenization: false, considerNormalization: false);
        }

        return GetHeuristicIndexByTokenCount(text, maxTokens);
    }

    private static int GetHeuristicIndexByTokenCount(string text, int maxTokens)
    {
        int current = EstimateHeuristic(text);
        if (current <= maxTokens) return text.Length;

        double budget = maxTokens;
        double cost = 0;
        for (int i = 0; i < text.Length; i++)
        {
            cost += IsCjk(text[i]) ? 1.0 : 0.25;
            if (cost >= budget) return i;
        }
        return text.Length;
    }
}
