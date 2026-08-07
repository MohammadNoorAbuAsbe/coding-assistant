using OpenAI.Chat;
using TerminalAiAssistant;
using Xunit;

namespace TerminalAiAssistant.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _dir;

    public SessionStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ca-session-tests", Guid.NewGuid().ToString("N"));
        SessionStore.StorageDir = _dir;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    private static ChatSession BuildSession()
    {
        var session = new ChatSession
        {
            Workspace = @"C:\repo\app"
        };
        session.Messages.Add(new SystemChatMessage("You are a coding assistant."));
        session.Messages.Add(new UserChatMessage("Add a sum function to Program.cs"));
        session.Messages.Add(new AssistantChatMessage("I'll read the file first."));
        var toolCall = ChatToolCall.CreateFunctionToolCall(
            "call_abc", "Read", BinaryData.FromString("{\"file_path\":\"Program.cs\"}"));
        session.Messages.Add(new AssistantChatMessage([toolCall]));
        session.Messages.Add(new ToolChatMessage("call_abc", "1: using System;"));
        session.Messages.Add(new AssistantChatMessage("Done."));
        session.SessionStarted = true;
        return session;
    }

    [Fact]
    public void SaveLoad_RoundTripsFullContext()
    {
        var session = BuildSession();
        SessionStore.Save(session);

        var loaded = SessionStore.Load(session.Id);
        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded!.Id);
        Assert.Equal(@"C:\repo\app", loaded.Workspace);
        Assert.True(loaded.SessionStarted);
        Assert.Equal(6, loaded.Messages.Count);
        Assert.Equal("system", loaded.Messages[0].Role);
        Assert.Equal("You are a coding assistant.", loaded.Messages[0].Text);
        Assert.Equal("user", loaded.Messages[1].Role);
        Assert.Equal("Add a sum function to Program.cs", loaded.Messages[1].Text);
        Assert.Equal("tool", loaded.Messages[4].Role);
        Assert.Equal("call_abc", loaded.Messages[4].ToolCallId);
        Assert.Equal("1: using System;", loaded.Messages[4].Text);
    }

    [Fact]
    public void SaveLoad_ReconstructsChatMessages()
    {
        var session = BuildSession();
        SessionStore.Save(session);

        var restored = SessionStore.ToSession(SessionStore.Load(session.Id)!);
        Assert.Equal(6, restored.Messages.Count);
        Assert.IsType<SystemChatMessage>(restored.Messages[0]);
        Assert.IsType<UserChatMessage>(restored.Messages[1]);
        Assert.IsType<AssistantChatMessage>(restored.Messages[2]);
        Assert.IsType<AssistantChatMessage>(restored.Messages[3]);
        Assert.IsType<ToolChatMessage>(restored.Messages[4]);
        Assert.IsType<AssistantChatMessage>(restored.Messages[5]);

        var toolMsg = Assert.IsType<ToolChatMessage>(restored.Messages[4]);
        Assert.Equal("call_abc", toolMsg.ToolCallId);
        Assert.Equal("1: using System;", ContextManager.ExtractText(toolMsg.Content));

        var toolCallMsg = Assert.IsType<AssistantChatMessage>(restored.Messages[3]);
        Assert.Single(toolCallMsg.ToolCalls);
        Assert.Equal("call_abc", toolCallMsg.ToolCalls[0].Id);
        Assert.Equal("Read", toolCallMsg.ToolCalls[0].FunctionName);
        Assert.Contains("Program.cs", toolCallMsg.ToolCalls[0].FunctionArguments!.ToString());
    }

    [Fact]
    public void Save_TitleDerivedFromFirstUserMessage()
    {
        var session = BuildSession();
        SessionStore.Save(session);
        Assert.Equal("Add a sum function to Program.cs", session.Title);
        Assert.Equal("Add a sum function to Program.cs", SessionStore.Load(session.Id)!.Title);
    }

    [Fact]
    public void Save_TitleCollapsedToSingleLineAndTruncated()
    {
        var session = new ChatSession();
        session.Messages.Add(new SystemChatMessage("sys"));
        string longPrompt = "Fix the crash " + new string('x', 200);
        session.Messages.Add(new UserChatMessage("first line\r\n" + longPrompt));
        SessionStore.Save(session);

        Assert.DoesNotContain("\n", session.Title);
        Assert.DoesNotContain("\r", session.Title);
        Assert.True(session.Title.Length <= 65);
        Assert.EndsWith("…", session.Title);
    }

    [Fact]
    public void Save_ExistingTitlePreserved()
    {
        var session = BuildSession();
        session.Title = "My custom session";
        SessionStore.Save(session);
        Assert.Equal("My custom session", session.Title);
        Assert.Equal("My custom session", SessionStore.Load(session.Id)!.Title);
    }

    [Fact]
    public void Save_EmptySessionTitleIsSession()
    {
        var session = new ChatSession();
        SessionStore.Save(session);
        Assert.Equal("Session", SessionStore.Load(session.Id)!.Title);
    }

    [Fact]
    public void Save_UpdatesUpdatedAt()
    {
        var session = BuildSession();
        SessionStore.Save(session);
        DateTimeOffset first = SessionStore.Load(session.Id)!.UpdatedAt;

        Thread.Sleep(50);
        SessionStore.Save(session);
        DateTimeOffset second = SessionStore.Load(session.Id)!.UpdatedAt;

        Assert.True(second >= first);
    }

    [Fact]
    public void List_OrdersMostRecentlyUpdatedFirst()
    {
        var a = BuildSession();
        var b = BuildSession();
        var c = BuildSession();
        SessionStore.Save(a);
        Thread.Sleep(20);
        SessionStore.Save(b);
        Thread.Sleep(20);
        SessionStore.Save(c);

        var list = SessionStore.List();
        Assert.Equal([c.Id, b.Id, a.Id], list.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void List_Empty_ReturnsEmpty()
    {
        Assert.Empty(SessionStore.List());
    }

    [Fact]
    public void Delete_RemovesSession()
    {
        var session = BuildSession();
        SessionStore.Save(session);
        Assert.True(SessionStore.Delete(session.Id));
        Assert.Null(SessionStore.Load(session.Id));
        Assert.False(SessionStore.Delete(session.Id));
    }

    [Fact]
    public void Load_MissingSession_ReturnsNull()
    {
        Assert.Null(SessionStore.Load(Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{not valid json!!!");
        Assert.Null(SessionStore.Load("corrupt"));
        Assert.Empty(SessionStore.List());
    }

    [Fact]
    public void Save_RoundTripsFileStateJournal()
    {
        var session = BuildSession();
        session.FileState.RecordRead(@"C:\repo\app\Program.cs", "using System;", 1, 10);
        session.FileState.RecordWrite(@"C:\repo\app\Helper.cs", "public class Helper {}");
        SessionStore.Save(session);

        var loaded = SessionStore.ToSession(SessionStore.Load(session.Id)!);
        Assert.True(loaded.FileState.HasState(@"C:\repo\app\Program.cs"));
        Assert.False(loaded.FileState.IsStale(@"C:\repo\app\Program.cs", "using System;"));
        Assert.True(loaded.FileState.TryGetReadCoverage(@"C:\repo\app\Program.cs", out int s, out int e));
        Assert.Equal((1, 10), (s, e));
        Assert.True(loaded.FileState.HasState(@"C:\repo\app\Helper.cs"));
        Assert.False(loaded.FileState.IsStale(@"C:\repo\app\Helper.cs", "public class Helper {}"));
        Assert.False(loaded.FileState.TryGetReadCoverage(@"C:\repo\app\Helper.cs", out _, out _)); // written, not read
    }

    [Fact]
    public void Save_HandlesCompactionSummaryMessage()
    {
        var session = BuildSession();
        session.Messages.Add(new UserChatMessage("[Session context (older messages trimmed)] old turns summarized here"));
        SessionStore.Save(session);

        var loaded = SessionStore.Load(session.Id)!;
        Assert.Equal(7, loaded.Messages.Count);
        Assert.Equal("user", loaded.Messages[^1].Role);
        Assert.StartsWith("[Session context", loaded.Messages[^1].Text);
    }
}
