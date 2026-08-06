namespace TerminalAiAssistant;

/// <summary>
/// Tracks real token usage reported by the API and calibrates the context
/// budget against it. Estimates (TokenEstimator) are never perfect — the API
/// reports the true input token count, and the ratio actual/estimated is
/// maintained as an exponential moving average. Truncation budgets are divided
/// by this factor so history is trimmed earlier when estimates undercount and
/// relaxed when they overcount. Statistics reset when the model changes and on
/// a new session (/new).
/// </summary>
public static class ContextUsageTracker
{
    private const double MinCorrection = 0.5;
    private const double MaxCorrection = 2.0;
    private const double EmaAlpha = 0.3;

    private static readonly object Gate = new();
    private static string? _model;
    private static bool _hasSamples;
    private static double _correctionEma = 1.0;
    private static long _totalInputTokens;
    private static long _totalOutputTokens;
    private static int _requestCount;

    /// <summary>
    /// Resets the calibration whenever the configured model changes, so stale
    /// ratios from a different tokenizer are never reused.
    /// </summary>
    public static void EnsureModel(string model)
    {
        lock (Gate)
        {
            if (string.Equals(_model, model, StringComparison.Ordinal)) return;
            _model = model;
            _hasSamples = false;
            _correctionEma = 1.0;
        }
    }

    /// <summary>
    /// Records one API request: the actual input tokens reported by the API
    /// and the estimated input tokens the assistant computed before sending.
    /// </summary>
    public static void Record(long actualInputTokens, long estimatedInputTokens)
    {
        if (actualInputTokens <= 0 || estimatedInputTokens <= 0) return;

        lock (Gate)
        {
            double ratio = Math.Clamp((double)actualInputTokens / estimatedInputTokens, MinCorrection, MaxCorrection);
            _correctionEma = _hasSamples
                ? EmaAlpha * ratio + (1 - EmaAlpha) * _correctionEma
                : ratio;
            _hasSamples = true;
            _totalInputTokens += actualInputTokens;
            _requestCount++;
        }
    }

    public static void RecordOutputTokens(long outputTokens)
    {
        if (outputTokens <= 0) return;
        lock (Gate)
        {
            _totalOutputTokens += outputTokens;
        }
    }

    /// <summary>
    /// The current actual/estimated correction factor, or 1.0 when no API
    /// usage has been observed yet. Clamped to [0.5, 2.0].
    /// </summary>
    public static double GetCorrectionFactor()
    {
        lock (Gate)
        {
            return _hasSamples ? _correctionEma : 1.0;
        }
    }

    /// <summary>
    /// Applies the correction factor to a raw token budget. When estimates
    /// undercount (factor &gt; 1) the budget is tightened; when they overcount
    /// (factor &lt; 1) it is loosened, so the same real token budget is
    /// preserved in both directions. Never returns less than 1.
    /// </summary>
    public static int GetAdjustedBudget(int rawBudget)
    {
        double factor = GetCorrectionFactor();
        if (factor <= 0) return rawBudget;
        return Math.Max(1, (int)Math.Round(rawBudget / factor));
    }

    public static (long TotalInputTokens, long TotalOutputTokens, int RequestCount) GetStats()
    {
        lock (Gate)
        {
            return (_totalInputTokens, _totalOutputTokens, _requestCount);
        }
    }

    public static void Reset()
    {
        lock (Gate)
        {
            _hasSamples = false;
            _correctionEma = 1.0;
            _totalInputTokens = 0;
            _totalOutputTokens = 0;
            _requestCount = 0;
        }
    }
}
