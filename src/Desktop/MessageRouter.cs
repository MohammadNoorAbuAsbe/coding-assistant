using System.Diagnostics;
using System.Text.Json;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Routes WebView2 → host messages to the agent engine and emits UI events.
/// Owns the session list, the active run's cancellation token, autopilot, the
/// file tree, settings persistence and undo handling.
/// </summary>
internal sealed class MessageRouter
{
    private sealed class SessionTab
    {
        public string Id => Session.Id;
        public ChatSession Session { get; }
        public string Title { get; set; }
        public bool SuppressSave { get; set; }

        public SessionTab(ChatSession session, string title)
        {
            Session = session;
            Title = title;
        }
    }

    private readonly DesktopUiSink _sink;
    private readonly Action<string> _postJson;
    private readonly List<SessionTab> _sessions = [];
    private SessionTab _active;
    private CancellationTokenSource? _runCts;
    private bool _busy;
    private AppBootstrapper.CancelController? _autopilotController;

    public bool IsBusy => _busy;

    /// <summary>Optional log hook (assigned by the host) for diagnostics.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Raised on the UI thread whenever the session tab set changes.</summary>
    public event Action? TabsChanged;

    public event Action<string>? ActiveSessionChanged;

    public MessageRouter(DesktopUiSink sink, Action<string> postJson)
    {
        _sink = sink;
        _postJson = postJson;

        // Restore the most recently used persisted session (with full context)
        // so a restart picks up where the user left off.
        var latest = SessionStore.List(1).FirstOrDefault(s => s.Messages.Count > 0);
        if (latest != null)
        {
            _active = new SessionTab(SessionStore.ToSession(latest), latest.Title);
            RefreshSystemPrompt(_active.Session);
        }
        else
        {
            _active = NewSessionTab();
        }
        _sessions.Add(_active);
    }

    private static SessionTab NewSessionTab() =>
        new(new ChatSession { Workspace = Environment.CurrentDirectory }, "Session");

    /// <summary>
    /// Replaces the persisted system prompt with the current provider's
    /// prompt so restored sessions always use fresh instructions.
    /// </summary>
    private static void RefreshSystemPrompt(ChatSession session)
    {
        var messages = session.Messages;
        if (messages.FirstOrDefault() is OpenAI.Chat.SystemChatMessage)
            messages.RemoveAt(0);
        messages.Insert(0, new OpenAI.Chat.SystemChatMessage(SystemPrompt.GetPrompt(Configuration.GetProvider())));
        session.SessionStarted = true;
    }

    public ChatSession ActiveSession => _active.Session;

    public IReadOnlyList<(string Id, string Title)> Tabs =>
        _sessions.Select(s => (s.Id, s.Title)).ToList();

    public string ActiveTabId => _active.Id;

    public void Handle(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl))
                return;
            string cmd = cmdEl.GetString() ?? "";

            Log?.Invoke("in: " + cmd);

            switch (cmd)
            {
                case "ui:ready":
                    OnUiReady();
                    break;
                case "ui:log":
                    Log?.Invoke("ui-log: " + (GetString(doc, "text") ?? ""));
                    break;
                case "send":
                    OnSend(GetString(doc, "text"));
                    break;
                case "stop":
                    OnStop();
                    break;
                case "session:new":
                    OnNewSession();
                    break;
                case "session:close":
                    OnCloseSession(GetString(doc, "id"));
                    break;
                case "session:select":
                    OnSelectSession(GetString(doc, "id"));
                    break;
                case "session:history":
                    OnShowHistory();
                    break;
                case "session:resume":
                    OnResumeSession(GetString(doc, "id"));
                    break;
                case "session:delete":
                    OnDeleteSession(GetString(doc, "id"));
                    break;
                case "files:list":
                    PostEvent("files:list", new { tree = FileTree.BuildRoot(Environment.CurrentDirectory) });
                    break;
                case "files:expand":
                    OnExpand(GetString(doc, "path"));
                    break;
                case "file:read":
                    OnFileRead(GetString(doc, "path"), doc.RootElement.TryGetProperty("maxBytes", out var mb) && mb.TryGetInt32(out int max) ? max : 262144);
                    break;
                case "folder:pick":
                    OnPickFolder();
                    break;
                case "settings:get":
                    PostSettings();
                    break;
                case "settings:set":
                    OnSetSettings(doc.RootElement);
                    break;
                case "undo:revert":
                    OnUndoRevert(doc.RootElement);
                    break;
                case "open:external":
                    OnOpenExternal(doc.RootElement);
                    break;
                case "question:answer":
                    OnQuestionAnswer(doc.RootElement);
                    break;
                case "autopilot:toggle":
                    OnToggleAutopilot();
                    break;
                case "exit":
                    System.Windows.Forms.Application.Exit();
                    Environment.Exit(0);
                    break;
            }
        }
    }

    // ── Handlers ────────────────────────────────────────────────────
    private void OnUiReady()
    {
        PostMeta();
        PostSettings();
        AppUi.PublishChanges(_active.Session.Undo);
        PostTranscript();
        PostEvent("files:list", new { tree = FileTree.BuildRoot(Environment.CurrentDirectory) });
    }

    private void OnSend(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _busy)
            return;

        _busy = true;
        var cts = new CancellationTokenSource();
        _runCts = cts;
        var session = _active.Session;

        PostEvent("status", new { text = "Thinking…", busy = true });

        // CancellationToken.None here so the task always runs (and the
        // finally always resets _busy), even if cts is already cancelled.
        Task.Run(async () =>
        {
            Diag.Log("router:task-start");
            try
            {
                await ChatOrchestrator.Run(session, text, cts.Token);
                Diag.Log("router:task-run-done");
            }
            catch (OperationCanceledException)
            {
                PostEvent("status", new { text = "Stopped" });
            }
            catch (Exception ex)
            {
                PostEvent("error", new { message = ex.Message });
            }
            finally
            {
                AppUi.PublishChanges(session.Undo);
                SaveSession(session);
                PostEvent("status", new { });
                _busy = false;
            }
        }, CancellationToken.None);
    }

    private void OnStop()
    {
        _runCts?.Cancel();
    }

    private void OnNewSession()
    {
        SaveActiveSession();
        var tab = NewSessionTab();
        _sessions.Add(tab);
        SelectTab(tab);
        ContextUsageTracker.Reset();
        PostMeta();
        AppUi.PublishChanges(tab.Session.Undo);
        TabsChanged?.Invoke();
    }

    private void OnCloseSession(string? id)
    {
        var tab = _sessions.FirstOrDefault(s => s.Id == id);
        if (tab == null || _sessions.Count <= 1)
            return;
        SaveSession(tab.Session);
        _sessions.Remove(tab);
        if (_active == tab)
            SelectTab(_sessions[^1]);
        TabsChanged?.Invoke();
    }

    private void OnSelectSession(string? id)
    {
        var tab = _sessions.FirstOrDefault(s => s.Id == id);
        if (tab != null && tab != _active)
            SelectTab(tab);
    }

    private void SelectTab(SessionTab tab)
    {
        _active = tab;
        ActiveSessionChanged?.Invoke(tab.Id);
        PostTranscript();
    }

    private void OnShowHistory()
    {
        // Flush open tabs so the list always reflects the latest state.
        foreach (var tab in _sessions)
            SaveSession(tab.Session);

        var items = SessionStore.List(SessionStore.MaxHistorySessions)
            .Where(s => s.Messages.Count > 0)
            .Select(s => new
            {
                id = s.Id,
                title = string.IsNullOrWhiteSpace(s.Title) ? "Untitled" : s.Title,
                created = s.CreatedAt.ToUnixTimeMilliseconds(),
                updated = s.UpdatedAt.ToUnixTimeMilliseconds(),
                turns = s.Messages.Count(m => m.Role == "user"),
                messages = s.Messages.Count,
                workspace = s.Workspace
            })
            .ToList();
        PostEvent("sessions", new { items });
    }

    private void OnResumeSession(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        var existing = _sessions.FirstOrDefault(s => s.Id == id);
        if (existing != null)
        {
            SelectTab(existing);
            return;
        }

        var stored = SessionStore.Load(id);
        if (stored == null)
        {
            PostEvent("toast", new { text = "Session not found on disk.", kind = "error" });
            return;
        }

        var tab = new SessionTab(SessionStore.ToSession(stored), stored.Title);
        RefreshSystemPrompt(tab.Session);
        _sessions.Add(tab);
        SelectTab(tab);
        TabsChanged?.Invoke();
        PostEvent("toast", new { text = "Session restored.", kind = "success" });
    }

    private void OnDeleteSession(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return;

        var tab = _sessions.FirstOrDefault(s => s.Id == id);
        if (tab != null)
        {
            tab.SuppressSave = true; // never resurrect a deleted session
            if (_sessions.Count > 1)
            {
                _sessions.Remove(tab);
                if (_active == tab)
                    SelectTab(_sessions[^1]);
                TabsChanged?.Invoke();
            }
        }

        bool deleted = SessionStore.Delete(id);
        PostEvent("toast", new
        {
            text = deleted ? "Session deleted." : "Session could not be deleted.",
            kind = deleted ? "success" : "error"
        });
        OnShowHistory();
    }

    /// <summary>Persists all open tabs (app exit).</summary>
    public void Shutdown()
    {
        foreach (var tab in _sessions)
        {
            if (!tab.SuppressSave)
                SaveSession(tab.Session);
        }
    }

    private void OnExpand(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            PostEvent("files:expand", new { error = "Folder not found." });
            return;
        }
        PostEvent("files:expand", new { path, nodes = FileTree.BuildChildren(path) });
    }

    private void OnFileRead(string? path, int maxBytes)
    {
        if (string.IsNullOrEmpty(path))
            return;
        var doc = FileTree.ReadFile(path, maxBytes);
        PostEvent("file:read", doc!.RootElement.Clone());
    }

    private void OnPickFolder()
    {
        var picked = FolderPicker.PickFolder(Environment.CurrentDirectory);
        if (picked == null)
            return;

        Environment.CurrentDirectory = picked;
        PostMeta();
        PostSettings();
        PostEvent("files:list", new { tree = FileTree.BuildRoot(picked) });
    }

    private void OnSetSettings(JsonElement element)
    {
        string? provider = null;
        string? model = null;
        if (element.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String)
            provider = p.GetString();
        if (element.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            model = m.GetString();

        if (provider != null && Configuration.Providers.ContainsKey(provider))
        {
            Configuration.SetProvider(provider);
            SettingsStore.Save(provider, null);
        }
        if (model != null)
        {
            Configuration.SetModel(model);
            SettingsStore.Save(null, model);
        }

        PostSettings();
        PostMeta();
    }

    private void OnUndoRevert(JsonElement element)
    {
        bool latest = element.TryGetProperty("latest", out var lt) && lt.ValueKind == JsonValueKind.True;
        var journal = _active.Session.Undo;

        UndoEntry? entry;
        if (latest)
        {
            entry = journal.UndoLast();
        }
        else if (element.TryGetProperty("index", out var idx) && idx.TryGetInt32(out int index))
        {
            entry = journal.UndoAt(index);
        }
        else if (element.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
        {
            var entries = journal.List();
            int i = entries.ToList().FindIndex(e => string.Equals(e.FullPath, pathEl.GetString(), StringComparison.OrdinalIgnoreCase));
            entry = i >= 0 ? journal.UndoAt(i) : null;
        }
        else
        {
            entry = null;
        }

        AppUi.PublishChanges(journal);
        if (entry == null)
        {
            PostEvent("toast", new { text = "Nothing to undo.", kind = "error" });
            return;
        }

        if (_active.Session.SessionStarted)
        {
            _active.Session.Messages.Add(new OpenAI.Chat.UserChatMessage(
                $"The user reverted a file change: {entry.FullPath} was {(entry.ExistedBefore ? "restored to its previous content" : "deleted (it did not exist before)")}. The tool call that made this change was {entry.ToolName}. Do not rely on previous tool results for this file — re-read it if needed."));
        }

        PostEvent("toast", new
        {
            text = entry.ExistedBefore ? $"Reverted {entry.FullPath}" : $"Deleted {entry.FullPath}",
            kind = "success"
        });
    }

    private void OnOpenExternal(JsonElement element)
    {
        try
        {
            if (element.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
            {
                Process.Start(new ProcessStartInfo(url.GetString()!) { UseShellExecute = true });
            }
            else if (element.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
            {
                string full = path.GetString()!;
                if (File.Exists(full))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = true });
                else if (Directory.Exists(full))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{full}\"") { UseShellExecute = true });
            }
        }
        catch
        {
            // Shell launch failed — ignore.
        }
    }

    private void OnQuestionAnswer(JsonElement element)
    {
        string id = GetString(element, "id") ?? "";
        string? value = null;
        if (element.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
            value = v.GetString();
        _sink.ResolveQuestion(id, value);
    }

    private void OnToggleAutopilot()
    {
        if (Autopilot.IsActive)
        {
            _autopilotController?.Cancel();
            return;
        }

        var session = _active.Session;
        var controller = new AppBootstrapper.CancelController();
        controller.Arm();
        _autopilotController = controller;

        Task.Run(async () =>
        {
            try
            {
                await Autopilot.Run(session, controller);
            }
            catch (Exception ex)
            {
                PostEvent("error", new { message = ex.Message });
            }
            finally
            {
                PostEvent("status", new { });
            }
        });
    }

    // ── Session persistence ────────────────────────────────────────
    private void SaveActiveSession()
    {
        SaveSession(_active.Session);
    }

    private void SaveSession(ChatSession session)
    {
        var tab = _sessions.FirstOrDefault(t => ReferenceEquals(t.Session, session));
        if (tab is { SuppressSave: true })
            return;

        // A session with only the system prompt has no conversation yet.
        if (session.Messages.Count <= 1)
            return;

        string before = session.Title;
        SessionStore.Save(session);

        if (tab != null && session.Title != before)
        {
            tab.Title = session.Title;
            TabsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Sends the active session's transcript to the UI so tabs and restored
    /// sessions can be re-rendered from the persisted context.
    /// </summary>
    private void PostTranscript()
    {
        var items = new List<object>();
        foreach (var msg in _active.Session.Messages)
        {
            switch (msg)
            {
                case OpenAI.Chat.SystemChatMessage:
                    break;
                case OpenAI.Chat.UserChatMessage user:
                    items.Add(new { role = "user", text = ContextManager.ExtractText(user.Content) });
                    break;
                case OpenAI.Chat.AssistantChatMessage assistant:
                    string? text = assistant.Content is { } ac ? ContextManager.ExtractText(ac) : null;
                    if (string.IsNullOrEmpty(text))
                        text = null;
                    var tools = assistant.ToolCalls?
                        .Select(t => new
                        {
                            name = t.FunctionName,
                            arg = ChatOrchestrator.ExtractPrimaryArg(t.FunctionName, t.FunctionArguments?.ToString() ?? "")
                        })
                        .ToList();
                    items.Add(new { role = "assistant", text, tools });
                    break;
                case OpenAI.Chat.ToolChatMessage:
                    break;
            }
        }
        PostEvent("session:messages", new { items });
    }

    // ── Helpers ─────────────────────────────────────────────────────
    private void PostMeta()
    {
        PostEvent("meta", new
        {
            provider = Configuration.GetProvider(),
            model = Configuration.GetModel(),
            context = Configuration.GetContextWindowSize(),
            workspace = Environment.CurrentDirectory
        });
    }

    private void PostSettings()
    {
        var provider = Configuration.GetProvider();
        var models = Configuration.Providers.TryGetValue(provider, out var cfg) ? cfg.Models : [];
        PostEvent("settings", new
        {
            provider,
            model = Configuration.GetModel(),
            providers = Configuration.Providers.Keys.ToList(),
            models,
            workspace = Environment.CurrentDirectory
        });
    }

    private void PostEvent(string type, object payload)
    {
        string json = JsonSerializer.Serialize(new { type, payload });
        _postJson(json);
    }

    private static string? GetString(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
