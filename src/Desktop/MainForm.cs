using System.Drawing.Drawing2D;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Borderless desktop shell: DWM rounded corners, custom title bar with
/// session tabs, and a WebView2 pane hosting the UI. All engine → UI events
/// flow through <see cref="PostToUi"/> which marshals to the UI thread.
/// </summary>
internal sealed class MainForm : Form
{
    private const int TitleBarHeight = 42;
    private const int TabHeight = 30;
    private const string HostName = "appui.local";

    private readonly WebView2 _web;
    private readonly MessageRouter _router;
    private readonly DesktopUiSink _sink;
    private readonly Panel _titleBar;
    private readonly Panel _tabs;
    private readonly Label _title;
    private readonly Button _btnMin;
    private readonly Button _btnMax;
    private readonly Button _btnClose;
    private readonly Button _btnNewTab;
    private bool _maximized;

    public MainForm()
    {
        Text = "Coding Assistant";
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.FromArgb(11, 13, 16);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1360, 860);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = new Font("Segoe UI Variable", 9F, FontStyle.Regular, GraphicsUnit.Point);

        _titleBar = BuildTitleBar();
        _tabs = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(2, 0, 0, 0)
        };
        _tabs.Resize += (_, _) => RenderTabs();

        _title = new Label
        {
            Text = "Coding Assistant",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI Variable", 9.5F, FontStyle.Bold),
            Padding = new Padding(14, 0, 0, 0),
            Size = new Size(170, TitleBarHeight),
            Dock = DockStyle.Left
        };

        _btnNewTab = MakeTitleButton("+", "New session (Ctrl+N)", 34);
        _btnMin = MakeTitleButton("─", "Minimize", 46);
        _btnMax = MakeTitleButton("▢", "Maximize / Restore", 46);
        _btnClose = MakeTitleButton("✕", "Close", 46, isClose: true);

        _titleBar.Controls.Add(_tabs);
        _titleBar.Controls.Add(_btnClose);
        _titleBar.Controls.Add(_btnMax);
        _titleBar.Controls.Add(_btnMin);
        _titleBar.Controls.Add(_btnNewTab);
        _titleBar.Controls.Add(_title);
        // Index 0 is docked last, so the tabs fill the middle after the
        // right-edge buttons and the left title label are placed.
        _titleBar.Controls.SetChildIndex(_tabs, 0);
        _titleBar.Controls.SetChildIndex(_title, 1);

        _web = new WebView2
        {
            Dock = DockStyle.Fill
        };

        Controls.Add(_web);
        Controls.Add(_titleBar);

        _sink = new DesktopUiSink(PostToUi);
        AppUi.Sink = _sink;
        FormClosed += (_, _) =>
        {
            _router?.Shutdown();
            if (ReferenceEquals(AppUi.Sink, _sink))
                AppUi.Sink = null;
        };
        _router = new MessageRouter(_sink, PostToUi);
        MessageRouter.Log = LogStartup;
        Diag.Hook = LogStartup;

        _btnMin.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _btnMax.Click += (_, _) => ToggleMaximize();
        _btnClose.Click += (_, _) => Close();
        _btnNewTab.Click += (_, _) => _router.Handle("{\"cmd\":\"session:new\"}");

        // Tab events can fire from engine threads (e.g. after a run finishes
        // and a session title is derived) — marshal to the UI thread.
        _router.TabsChanged += () => InvokeOnUi(() => RenderTabs());
        _router.ActiveSessionChanged += _ => InvokeOnUi(() => RenderTabs());

        Load += async (_, _) => await InitializeWebViewAsync();
        Shown += (_, _) => RenderTabs();
    }

    // ── Title bar construction ──────────────────────────────────────
    private Panel BuildTitleBar()
    {
        var bar = new CaptionPanel
        {
            Dock = DockStyle.Top,
            Height = TitleBarHeight,
            BackColor = Color.FromArgb(13, 16, 21)
        };
        return bar;
    }

    /// <summary>Panel that reports HTCAPTION for empty areas so the window
    /// can be dragged from the tab strip without stealing clicks from the
    /// tab buttons (which are separate child windows).</summary>
    private sealed class CaptionPanel : Panel
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_NCHITTEST)
            {
                m.Result = (IntPtr)NativeMethods.HTCAPTION;
                return;
            }
            base.WndProc(ref m);
        }
    }

    private Button MakeTitleButton(string glyph, string tooltip, int width, bool isClose = false)
    {
        var b = new Button
        {
            Text = glyph,
            Width = width,
            Height = TitleBarHeight,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = isClose
            ? Color.FromArgb(196, 43, 28)
            : Color.FromArgb(30, 41, 59);
        b.FlatAppearance.MouseDownBackColor = isClose
            ? Color.FromArgb(185, 28, 28)
            : Color.FromArgb(15, 23, 42);
        b.ForeColor = Color.FromArgb(203, 213, 225);
        b.Font = new Font("Segoe UI Variable", 11F, FontStyle.Regular);
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.TabStop = false;
        new ToolTip().SetToolTip(b, tooltip);
        return b;
    }

    private void RenderTabs()
    {
        if (_router == null)
            return;

        _tabs.SuspendLayout();
        _tabs.Controls.Clear();

        int x = 0;
        foreach (var (id, title) in _router.Tabs)
        {
            var tab = new Button
            {
                Text = title,
                AutoSize = false,
                Height = TabHeight,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                TabStop = false,
                Margin = new Padding(0),
                Padding = new Padding(12, 0, 12, 0)
            };
            int w = TextRenderer.MeasureText(title, tab.Font).Width + 44;
            tab.Width = Math.Max(96, Math.Min(200, w));
            tab.Location = new Point(x, (TitleBarHeight - TabHeight) / 2);
            tab.FlatAppearance.BorderSize = 0;
            bool isActive = id == _router.ActiveTabId;
            tab.BackColor = isActive ? Color.FromArgb(24, 30, 41) : Color.Transparent;
            tab.ForeColor = isActive ? Color.FromArgb(226, 232, 240) : Color.FromArgb(120, 135, 155);
            tab.Font = new Font("Segoe UI Variable", 9F, isActive ? FontStyle.Bold : FontStyle.Regular);
            tab.Click += (_, _) =>
            {
                if (!isActive)
                    _router.Handle("{\"cmd\":\"session:select\",\"id\":\"" + id + "\"}");
            };
            _tabs.Controls.Add(tab);
            x += tab.Width + 4;
        }

        _tabs.ResumeLayout();
    }

    private void ToggleMaximize()
    {
        if (_maximized)
        {
            WindowState = FormWindowState.Normal;
            _btnMax.Text = "▢";
        }
        else
        {
            WindowState = FormWindowState.Maximized;
            _btnMax.Text = "❐";
        }
        _maximized = WindowState == FormWindowState.Maximized;
    }

    private void InvokeOnUi(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (InvokeRequired)
                BeginInvoke(action);
            else
                action();
        }
        catch
        {
            // Form is closing or handle is gone — skip the refresh.
        }
    }

    // ── WebView2 ────────────────────────────────────────────────────
    private async Task InitializeWebViewAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodingAssistant",
                "webview2");

            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            await _web.EnsureCoreWebView2Async(env);

            string webRoot = EmbeddedWeb.ExtractToCache();
            LogStartup("webroot=" + webRoot);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HostName, webRoot, CoreWebView2HostResourceAccessKind.Allow);

            _web.DefaultBackgroundColor = Color.FromArgb(11, 13, 16);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    _router.Handle(e.WebMessageAsJson);
                }
                catch (Exception ex)
                {
                    LogStartup("in: message error: " + ex);
                }
            };

            _web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess)
                {
                    LogStartup("navigation failed: " + e.WebErrorStatus);
                    return;
                }
                try
                {
                    string js = "JSON.stringify({title:document.title,app:!!document.getElementById('app'),hero:!!document.querySelector('.hero'),scripts:document.scripts.length})";
                    string res = await _web.CoreWebView2.ExecuteScriptAsync(js);
                    LogStartup("ui-ready " + res);
                }
                catch (Exception ex)
                {
                    LogStartup("ui-check error: " + ex.Message);
                }
            };

            _web.CoreWebView2.Navigate($"https://{HostName}/index.html");
            LogStartup("navigating to " + _web.Source);

            _ = ProbeUiAsync();

            if (Environment.GetCommandLineArgs().Contains("--selftest"))
                _ = RunSelfTestAsync();
        }
        catch (Exception ex)
        {
            LogStartup("EXCEPTION: " + ex);
            MessageBox.Show(
                "Coding Assistant could not start its UI.\n\n" + ex.Message +
                "\n\nMake sure the WebView2 Runtime is installed.",
                "Coding Assistant", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task RunSelfTestAsync()
    {
        try
        {
            await Task.Delay(9000);
            if (_web?.CoreWebView2 == null)
            {
                LogStartup("selftest: no webview");
                return;
            }
            string r = await _web.CoreWebView2.ExecuteScriptAsync(
                "(function(){var i=document.getElementById('input');if(!i)return 'no-input';i.value='hi';i.dispatchEvent(new Event('input'));var b=document.getElementById('btn-send');if(!b)return 'no-btn';b.click();return 'clicked';})()");
            LogStartup("selftest: " + r);
            await Task.Delay(20000);
            string dump1 = await _web.CoreWebView2.ExecuteScriptAsync(
                "(function(){return JSON.stringify({msgs:document.querySelectorAll('.msg').length,assistants:document.querySelectorAll('.msg.assistant').length,statusHidden:document.getElementById('sb-status').classList.contains('hidden')});})()");
            LogStartup("selftest-dump1: " + dump1);

            r = await _web.CoreWebView2.ExecuteScriptAsync(
                "(function(){var i=document.getElementById('input');if(!i)return 'no-input';i.value='and again';i.dispatchEvent(new Event('input'));var b=document.getElementById('btn-send');if(!b)return 'no-btn';b.click();return 'clicked2';})()");
            LogStartup("selftest2: " + r);
            await Task.Delay(20000);
            string dump2 = await _web.CoreWebView2.ExecuteScriptAsync(
                "(function(){return JSON.stringify({msgs:document.querySelectorAll('.msg').length,assistants:document.querySelectorAll('.msg.assistant').length,statusHidden:document.getElementById('sb-status').classList.contains('hidden')});})()");
            LogStartup("selftest-dump2: " + dump2);
        }
        catch (Exception ex)
        {
            LogStartup("selftest error: " + ex.Message);
        }
    }

    private async Task ProbeUiAsync()
    {
        try
        {
            await Task.Delay(6000);
            if (_web.CoreWebView2 == null)
                return;
            LogStartup("probe source=" + _web.Source);
            string js = "JSON.stringify({title:document.title,app:!!document.getElementById('app'),hero:!!document.querySelector('.hero'),scripts:document.scripts.length,readyState:document.readyState})";
            string res = await _web.CoreWebView2.ExecuteScriptAsync(js);
            LogStartup("probe result " + res);
        }
        catch (Exception ex)
        {
            LogStartup("probe error: " + ex.Message);
        }
    }

    // ── Window chrome ───────────────────────────────────────────────
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // Borderless hybrid: thick frame gives native resize edges and a
            // drop shadow; no caption is drawn so the window stays frameless.
            cp.Style |= 0x00040000; // WS_THICKFRAME
            cp.Style |= 0x00020000; // WS_MINIMIZEBOX
            cp.Style |= 0x00010000; // WS_MAXIMIZEBOX
            return cp;
        }
    }

    private static void LogStartup(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodingAssistant"));
            File.AppendAllText(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodingAssistant", "startup.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + "\n");
        }
        catch
        {
            // Logging must never break startup.
        }
    }

    /// <summary>
    /// Posts an event envelope to the webview. Events arrive from arbitrary
    /// engine threads. The CoreWebView2 property itself throws when accessed
    /// from a non-UI thread, so it must never be touched here — the webview
    /// null-check and the actual post happen in <see cref="PostCore"/>, which
    /// is only ever executed on the UI thread (via the form's BeginInvoke).
    /// </summary>
    private void PostToUi(string json)
    {
        if (_web == null)
            return;

        try
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => PostCore(json)));
                return;
            }
        }
        catch
        {
            // Handle not created yet — the webview can't be ready either,
            // so there is nothing safe to post from this thread.
            return;
        }

        PostCore(json);
    }

    private void PostCore(string json)
    {
        try
        {
            if (_web?.CoreWebView2 == null)
                return;
            LogEventType(json);
            _web.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch
        {
            // Webview not ready yet — drop the event.
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _eventCounts = new();

    private void LogEventType(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()!
                : "?";
            int n = _eventCounts.AddOrUpdate(type, 1, (_, v) => v + 1);
            if (n == 1 || n % 50 == 0)
                LogStartup("out: " + type + " (n=" + n + ")");
        }
        catch
        {
            // Never let logging break event delivery.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyCornerPreference(Handle, round: true);
        NativeMethods.ApplyDarkTitleBar(Handle, dark: true);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case NativeMethods.WM_GETMINMAXINFO:
                {
                    var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(m.LParam);
                    var work = Screen.FromHandle(Handle).WorkingArea;
                    mmi.ptMaxSize = new System.Drawing.Point(work.Width, work.Height);
                    mmi.ptMaxPosition = new System.Drawing.Point(work.Left, work.Top);
                    mmi.ptMinTrackSize = new System.Drawing.Point(900, 600);
                    System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, m.LParam, true);
                    m.Result = IntPtr.Zero;
                    return;
                }
            case NativeMethods.WM_NCHITTEST:
                {
                    int lParam = m.LParam.ToInt32();
                    int x = (short)(lParam & 0xFFFF);
                    int y = (short)((lParam >> 16) & 0xFFFF);
                    var point = PointToClient(new System.Drawing.Point(x, y));

                    if (point.Y <= TitleBarHeight)
                    {
                        var overButton = _titleBar.Controls.Cast<Control>()
                            .Any(c => c != _tabs && c.Visible && c.Bounds.Contains(point));
                        if (overButton)
                        {
                            m.Result = (IntPtr)NativeMethods.HTCLIENT;
                            return;
                        }
                        m.Result = (IntPtr)NativeMethods.HTCAPTION;
                        return;
                    }

                    base.WndProc(ref m);
                    return;
                }
            default:
                base.WndProc(ref m);
                return;
        }
    }
}
