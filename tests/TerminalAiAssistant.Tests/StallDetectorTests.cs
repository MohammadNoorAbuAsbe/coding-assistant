using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class StallDetectorTests
{
    private static ChatMessage AssistantTurn(string functionName, string argsJson) =>
        new AssistantChatMessage([ChatToolCall.CreateFunctionToolCall("id", functionName, BinaryData.FromString(argsJson))]);

    private static ToolChatMessage Result(string content) =>
        new ToolChatMessage("id", content);

    private static List<ChatMessage> Turn(string functionName, string argsJson, string? resultContent = null)
    {
        var messages = new List<ChatMessage> { AssistantTurn(functionName, argsJson) };
        if (resultContent != null)
        {
            messages.Add(Result(resultContent));
        }
        return messages;
    }

    private static string ReadArgs(string path, string? start = null, string? end = null)
    {
        var parts = new List<string> { $"\"file_path\": \"{path}\"" };
        if (start != null) parts.Add($"\"start_line\": \"{start}\"");
        if (end != null) parts.Add($"\"end_line\": \"{end}\"");
        return "{" + string.Join(", ", parts) + "}";
    }

    [Fact]
    public void InterruptsAfterThreeIdenticalCalls()
    {
        var tracker = new StallTracker();
        var fp = StallDetector.Fingerprint(Turn("Read", ReadArgs("src/Foo.cs"), "[Read skipped] same notice"));

        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
        Assert.True(tracker.Observe(fp));
    }

    [Fact]
    public void CatchesAlternatingLoop()
    {
        var tracker = new StallTracker();

        var readA = StallDetector.Fingerprint(Turn("Read", ReadArgs("src/A.cs"), "content A"));
        var grepTrue = StallDetector.Fingerprint(Turn("Grep", "{\"pattern\": \"true\"}", "matches"));

        Assert.False(tracker.Observe(readA));
        Assert.False(tracker.Observe(grepTrue));
        Assert.False(tracker.Observe(readA));
        Assert.False(tracker.Observe(grepTrue));
        Assert.True(tracker.Observe(readA));
    }

    [Fact]
    public void DifferentFiles_DoNotTrigger()
    {
        var tracker = new StallTracker();

        foreach (var path in new[] { "src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs", "src/E.cs" })
        {
            Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Read", ReadArgs(path), "content"))));
        }
    }

    [Fact]
    public void SameCallDifferentResult_IsProgress_NotAStall()
    {
        var tracker = new StallTracker();

        // Re-reading the same range but the file content changed each time
        // (e.g. the model edited it in between) is legitimate work.
        Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Read", ReadArgs("src/Foo.cs", "100", "200"), "old content"))));
        Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Read", ReadArgs("src/Foo.cs", "100", "200"), "new content"))));
        Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Read", ReadArgs("src/Foo.cs", "100", "200"), "newer content"))));
    }

    [Fact]
    public void SequentialEditsToSameFile_DoNotTrigger()
    {
        var tracker = new StallTracker();

        // Three different edits to the same file: legitimate multi-edit work.
        Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Edit",
            "{\"file_path\": \"src/Foo.cs\", \"old_string\": \"first target\", \"new_string\": \"one\"}", "ok 1"))));
        Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Edit",
            "{\"file_path\": \"src/Foo.cs\", \"old_string\": \"second target\", \"new_string\": \"two\"}", "ok 2"))));
        Assert.False(tracker.Observe(StallDetector.Fingerprint(Turn("Edit",
            "{\"file_path\": \"src/Foo.cs\", \"old_string\": \"third target\", \"new_string\": \"three\"}", "ok 3"))));
    }

    [Fact]
    public void RetryingTheSameFailedEdit_Triggers()
    {
        var tracker = new StallTracker();

        var fp = StallDetector.Fingerprint(Turn("Edit",
            "{\"file_path\": \"src/Foo.cs\", \"old_string\": \"same target\", \"new_string\": \"same fix\"}", "Error: old_string not found"));

        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
        Assert.True(tracker.Observe(fp));
    }

    [Fact]
    public void WindowClearsAfterIntervention_SoStallCanFireAgain()
    {
        var tracker = new StallTracker();

        var fp = StallDetector.Fingerprint(Turn("Edit", "{\"file_path\": \"src/Foo.cs\", \"old_string\": \"x\", \"new_string\": \"y\"}", "Error: old_string not found"));
        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
        Assert.True(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
        Assert.True(tracker.Observe(fp));
    }

    [Fact]
    public void TextOnlyTurn_ClearsTheWindow()
    {
        var tracker = new StallTracker();

        var fp = StallDetector.Fingerprint(Turn("Read", ReadArgs("src/Foo.cs"), "content"));
        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(null));
        Assert.False(tracker.Observe(fp));
        Assert.False(tracker.Observe(fp));
    }

    [Fact]
    public void Fingerprint_ReadIncludesPathAndRange()
    {
        // start_line streamed before file_path must not confuse the fingerprint.
        var msg = AssistantTurn("Read", "{\"start_line\": \"200\", \"file_path\": \"src/A.cs\"}");
        string fp = StallDetector.Fingerprint([msg])!;

        Assert.StartsWith("Read|src/A.cs|200-?", fp);
    }

    [Fact]
    public void Fingerprint_EditIncludesOldStringHash_NotJustPath()
    {
        var editA = StallDetector.Fingerprint(Turn("Edit", "{\"file_path\": \"src/A.cs\", \"old_string\": \"aaa\", \"new_string\": \"zzz\"}", "ok"));
        var editB = StallDetector.Fingerprint(Turn("Edit", "{\"file_path\": \"src/A.cs\", \"old_string\": \"bbb\", \"new_string\": \"zzz\"}", "ok"));

        Assert.NotNull(editA);
        Assert.NotNull(editB);
        Assert.NotEqual(editA, editB);
    }

    [Fact]
    public void Fingerprint_UsesPatternForGrep()
    {
        var msg = AssistantTurn("Grep", "{\"pattern\": \"TODO\", \"include\": \"*.cs\"}");
        string fp = StallDetector.Fingerprint([msg])!;

        Assert.StartsWith("Grep|TODO|*.cs|", fp);
    }

    [Fact]
    public void TextOnlyMessage_HasNoFingerprint()
    {
        Assert.Null(StallDetector.Fingerprint([new AssistantChatMessage("just text")]));
    }
}
