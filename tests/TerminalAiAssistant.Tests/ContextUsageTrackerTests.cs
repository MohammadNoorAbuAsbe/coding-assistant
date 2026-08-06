using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class ContextUsageTrackerTests
{
    public ContextUsageTrackerTests()
    {
        ContextUsageTracker.Reset();
    }

    [Fact]
    public void NoSamples_FactorIsOne_BudgetUnchanged()
    {
        Assert.Equal(1.0, ContextUsageTracker.GetCorrectionFactor());
        Assert.Equal(1000, ContextUsageTracker.GetAdjustedBudget(1000));
    }

    [Fact]
    public void Record_Underestimates_TightensBudget()
    {
        // Actual > estimated => the estimator undercounts => trim earlier.
        ContextUsageTracker.Record(actualInputTokens: 2000, estimatedInputTokens: 1000);

        Assert.Equal(2.0, ContextUsageTracker.GetCorrectionFactor());
        Assert.Equal(500, ContextUsageTracker.GetAdjustedBudget(1000));
    }

    [Fact]
    public void Record_Overestimates_LoosensBudget()
    {
        // Actual < estimated => the estimator overcounts => can afford more.
        ContextUsageTracker.Record(actualInputTokens: 500, estimatedInputTokens: 1000);

        Assert.Equal(0.5, ContextUsageTracker.GetCorrectionFactor());
        Assert.Equal(2000, ContextUsageTracker.GetAdjustedBudget(1000));
    }

    [Fact]
    public void Record_RatioAboveRange_ClampedToMax()
    {
        ContextUsageTracker.Record(actualInputTokens: 100_000, estimatedInputTokens: 100);
        Assert.Equal(2.0, ContextUsageTracker.GetCorrectionFactor());
    }

    [Fact]
    public void Record_RatioBelowRange_ClampedToMin()
    {
        ContextUsageTracker.Record(actualInputTokens: 1, estimatedInputTokens: 100_000);
        Assert.Equal(0.5, ContextUsageTracker.GetCorrectionFactor());
    }

    [Fact]
    public void Record_MultipleSamples_EmaAverages()
    {
        ContextUsageTracker.Record(actualInputTokens: 100, estimatedInputTokens: 100); // ratio 1.0
        ContextUsageTracker.Record(actualInputTokens: 200, estimatedInputTokens: 100); // ratio 2.0

        double expected = 0.3 * 2.0 + 0.7 * 1.0;
        Assert.Equal(expected, ContextUsageTracker.GetCorrectionFactor(), precision: 4);
    }

    [Fact]
    public void Record_NonPositiveValues_Ignored()
    {
        ContextUsageTracker.Record(actualInputTokens: 0, estimatedInputTokens: 100);
        ContextUsageTracker.Record(actualInputTokens: 100, estimatedInputTokens: 0);
        ContextUsageTracker.RecordOutputTokens(0);

        Assert.Equal(1.0, ContextUsageTracker.GetCorrectionFactor());
        Assert.Equal((0, 0, 0), ContextUsageTracker.GetStats());
    }

    [Fact]
    public void RecordOutputTokens_Accumulates()
    {
        ContextUsageTracker.RecordOutputTokens(100);
        ContextUsageTracker.RecordOutputTokens(250);

        Assert.Equal(350, ContextUsageTracker.GetStats().TotalOutputTokens);
    }

    [Fact]
    public void Stats_ReflectRequests()
    {
        ContextUsageTracker.Record(1000, 900);
        ContextUsageTracker.Record(2000, 1800);
        ContextUsageTracker.RecordOutputTokens(500);

        var stats = ContextUsageTracker.GetStats();
        Assert.Equal(3000, stats.TotalInputTokens);
        Assert.Equal(500, stats.TotalOutputTokens);
        Assert.Equal(2, stats.RequestCount);
    }

    [Fact]
    public void EnsureModel_ModelChange_ResetsCalibration()
    {
        ContextUsageTracker.Record(2000, 1000);
        Assert.Equal(2.0, ContextUsageTracker.GetCorrectionFactor());

        ContextUsageTracker.EnsureModel("gpt-4o");
        Assert.Equal(1.0, ContextUsageTracker.GetCorrectionFactor());

        // Same model again keeps the samples.
        ContextUsageTracker.Record(2000, 1000);
        ContextUsageTracker.EnsureModel("gpt-4o");
        Assert.Equal(2.0, ContextUsageTracker.GetCorrectionFactor());
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        ContextUsageTracker.Record(2000, 1000);
        ContextUsageTracker.RecordOutputTokens(50);

        ContextUsageTracker.Reset();

        Assert.Equal(1.0, ContextUsageTracker.GetCorrectionFactor());
        Assert.Equal((0, 0, 0), ContextUsageTracker.GetStats());
        Assert.Equal(1000, ContextUsageTracker.GetAdjustedBudget(1000));
    }

    [Fact]
    public void GetAdjustedBudget_NeverBelowOne()
    {
        ContextUsageTracker.Record(actualInputTokens: 100_000, estimatedInputTokens: 100); // factor 2.0
        Assert.Equal(1, ContextUsageTracker.GetAdjustedBudget(1));
    }
}
