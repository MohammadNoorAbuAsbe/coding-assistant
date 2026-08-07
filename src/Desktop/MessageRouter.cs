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
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public ChatSession Session { get; } = new();
        public string Title { get; set; } = "Session";
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
        _active = new SessionTab { Title = "Session" };
        _sessions.Add(_active);
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
                    PostEvent("toast", new { text = "Sessions live in memory for this run; history is not persisted yet.", kind = "info" });
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
        AppUi.PublishChanges();
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
                AppUi.PublishChanges();
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
        var tab = new SessionTab();
        _sessions.Add(tab);
        SelectTab(tab);
        UndoJournal.Clear();
        FileStateJournal.Clear();
        ContextUsageTracker.Reset();
        PostMeta();
        AppUi.PublishChanges();
        TabsChanged?.Invoke();
    }

    private void OnCloseSession(string? id)
    {
        var tab = _sessions.FirstOrDefault(s => s.Id == id);
        if (tab == null || _sessions.Count <= 1)
            return;
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

        UndoEntry? entry;
        if (latest)
        {
            entry = UndoJournal.UndoLast();
        }
        else if (element.TryGetProperty("index", out var idx) && idx.TryGetInt32(out int index))
        {
            entry = UndoJournal.UndoAt(index);
        }
        else if (element.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
        {
            var entries = UndoJournal.List();
            int i = entries.ToList().FindIndex(e => string.Equals(e.FullPath, pathEl.GetString(), StringComparison.OrdinalIgnoreCase));
            entry = i >= 0 ? UndoJournal.UndoAt(i) : null;
        }
        else
        {
            entry = null;
        }

        AppUi.PublishChanges();
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
