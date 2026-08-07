using System.Collections.Concurrent;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Bridges the engine's event bus to the WebView2 UI. Events are serialized
/// by <see cref="AppUi"/> into JSON envelopes and posted to the webview on the
/// UI thread. Questions asked by the agent are surfaced as a modal; the
/// answer arrives back through <see cref="ResolveQuestion"/>.
/// </summary>
internal sealed class DesktopUiSink : IAppUiSink
{
    private readonly Action<string> _postToUi;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingQuestions = new();

    public DesktopUiSink(Action<string> postToUi)
    {
        _postToUi = postToUi;
    }

    public void Send(string jsonMessage)
    {
        _postToUi(jsonMessage);
    }

    public Task<string> AskQuestionAsync(
        string id, string question, string? header,
        IReadOnlyList<ToolHandler.QuestionOption> options,
        bool allowCustom, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingQuestions[id] = tcs;

        var payload = new
        {
            id,
            question,
            header,
            options = options.Select(o => o.label).ToList(),
            allowCustom
        };
        Send(System.Text.Json.JsonSerializer.Serialize(
            new { type = "question", payload }));

        cancellationToken.Register(() => tcs.TrySetResult("User did not provide an answer."));
        return tcs.Task;
    }

    /// <summary>Called when the UI answers a pending question (question:answer).</summary>
    public bool ResolveQuestion(string? id, string? value)
    {
        if (!string.IsNullOrEmpty(id) && _pendingQuestions.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult(string.IsNullOrEmpty(value) ? "User did not provide an answer." : value);
            return true;
        }

        // The UI may answer without an id (custom input) — resolve the newest
        // outstanding question as a fallback.
        if (_pendingQuestions.Count == 0)
            return false;

        string? last = _pendingQuestions.Keys.Max();
        if (last != null && _pendingQuestions.TryRemove(last, out tcs))
        {
            tcs.TrySetResult(string.IsNullOrEmpty(value) ? "User did not provide an answer." : value);
            return true;
        }

        return false;
    }
}
