using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAI.Chat;

namespace TerminalAiAssistant;

/// <summary>
/// Optional UI sink. When a desktop UI is attached (WebView2 host), it
/// installs a sink and receives every structured event the engine emits.
/// The terminal experience keeps working unchanged when no sink is present.
/// </summary>
public interface IAppUiSink
{
    void Send(string jsonMessage);

    /// <summary>Asks the user a multiple-choice question. Returns the answer text.</summary>
    Task<string> AskQuestionAsync(
        string id, string question, string? header,
        IReadOnlyList<ToolHandler.QuestionOption> options,
        bool allowCustom, CancellationToken cancellationToken);
}

/// <summary>
/// Event bus between the agent engine and the desktop UI. Publishing is a
/// no-op (cheap) when no UI is attached, so the terminal mode has zero cost.
/// </summary>
public static class AppUi
{
    public static IAppUiSink? Sink { get; set; }

    public static bool HasUi => Sink != null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> SendCounts = new();

    public static void Send(string type, object payload)
    {
        if (Sink is not { } sink)
        {
            LogSend(type, "sink=null");
            return;
        }

        string json;
        try
        {
            json = JsonSerializer.Serialize(new UiEventEnvelope { Type = type, Payload = payload }, JsonOptions);
        }
        catch (Exception ex)
        {
            LogSend(type, "serialize-error: " + ex.Message);
            return;
        }

        try
        {
            sink.Send(json);
            LogSend(type, "ok");
        }
        catch (Exception ex)
        {
            LogSend(type, "sink-error: " + ex.Message);
        }
    }

    private static void LogSend(string type, string note)
    {
        if (note == "ok")
        {
            int n = SendCounts.AddOrUpdate(type, 1, (_, v) => v + 1);
            if (n > 1)
                return;
        }
        Diag.Log("AppUi.Send " + type + " " + note);
    }

    /// <summary>
    /// Asks the user a question through the attached UI (modal). Without a UI
    /// there is no way to prompt, so the model gets a neutral non-answer.
    /// </summary>
    public static async Task<string> AskQuestionAsync(
        string id, string question, string? header,
        IReadOnlyList<ToolHandler.QuestionOption> options,
        bool allowCustom, CancellationToken cancellationToken)
    {
        if (Sink is { } sink)
        {
            try
            {
                return await sink.AskQuestionAsync(id, question, header, options, allowCustom, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return "User did not provide an answer.";
            }
        }

        return "User did not provide an answer.";
    }

    /// <summary>Publishes the undo journal so the UI can render the Changes panel.
    /// Falls back to the active run's journal (or the shared one) when no
    /// journal is passed explicitly.</summary>
    public static void PublishChanges(UndoJournal? journal = null)
    {
        journal ??= SessionContext.Undo;
        var entries = journal.List();
        var items = entries.Select((e, i) => new
        {
            index = i,
            path = e.FullPath,
            tool = e.ToolName,
            time = e.Timestamp.ToString("HH:mm:ss"),
            existed = e.ExistedBefore
        }).ToList();
        Send("changes", new { items });
    }
}

internal sealed class UiEventEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    public object? Payload { get; set; }
}
