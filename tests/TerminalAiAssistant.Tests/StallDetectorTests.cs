using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class StallDetectorTests
{
    private static ChatMessage AssistantTurn(string functionName, string arg) =>
        new AssistantChatMessage([ChatToolCall.CreateFunctionToolCall("id", functionName, BinaryData.FromString($"{{\"file_path\": \"{arg}\"}}"))]);

    [Fact]
    public void InterruptsAfterThreeConsecutiveIdenticalCalls()
    {
        string? last = null;
        int repeats = 0;

        var fp1 = StallDetector.Fingerprint([AssistantTurn("Read", "src/Foo.cs")]);
        var fp2 = StallDetector.Fingerprint([AssistantTurn("Read", "src/Foo.cs")]);
        var fp3 = StallDetector.Fingerprint([AssistantTurn("Read", "src/Foo.cs")]);

        Assert.False(StallDetector.ShouldInterruptStall(fp1, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(fp2, ref last, ref repeats));
        Assert.True(StallDetector.ShouldInterruptStall(fp3, ref last, ref repeats));
    }

    [Fact]
    public void SameFunctionDifferentArgument_IsNotAStall()
    {
        string? last = null;
        int repeats = 0;

        Assert.False(StallDetector.ShouldInterruptStall(
            StallDetector.Fingerprint([AssistantTurn("Read", "src/A.cs")]), ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(
            StallDetector.Fingerprint([AssistantTurn("Read", "src/B.cs")]), ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(
            StallDetector.Fingerprint([AssistantTurn("Read", "src/C.cs")]), ref last, ref repeats));
        Assert.Equal(1, repeats);
    }

    [Fact]
    public void InterruptedSequence_ResetsTracker()
    {
        string? last = null;
        int repeats = 0;

        var fp = StallDetector.Fingerprint([AssistantTurn("Edit", "src/Foo.cs")]);

        Assert.False(StallDetector.ShouldInterruptStall(fp, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(fp, ref last, ref repeats));
        Assert.True(StallDetector.ShouldInterruptStall(fp, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(fp, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(fp, ref last, ref repeats));
    }

    [Fact]
    public void AChangedCallBreaksTheRepeatRun()
    {
        string? last = null;
        int repeats = 0;

        var readA = StallDetector.Fingerprint([AssistantTurn("Read", "src/Foo.cs")]);
        var editB = StallDetector.Fingerprint([AssistantTurn("Edit", "src/Bar.cs")]);

        Assert.False(StallDetector.ShouldInterruptStall(readA, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(readA, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(editB, ref last, ref repeats));
        Assert.False(StallDetector.ShouldInterruptStall(readA, ref last, ref repeats));
        Assert.Equal(1, repeats);
    }

    [Fact]
    public void TextOnlyMessage_HasNoFingerprint_AndDoesNotTrigger()
    {
        string? last = "Read|src/Foo.cs";
        int repeats = 2;

        Assert.Null(StallDetector.Fingerprint([new AssistantChatMessage("just text")]));
        Assert.False(StallDetector.ShouldInterruptStall(null, ref last, ref repeats));
    }
}
