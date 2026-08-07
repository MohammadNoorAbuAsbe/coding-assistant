namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Desktop (GUI) entry point. Launches the borderless WebView2 shell; the
/// agent engine runs behind it.
/// </summary>
internal static class DesktopLauncher
{
    public static async Task<int> RunAsync(string[] args)
    {
        Configuration.LoadEnvFile();
        Configuration.LoadProviderConfigs();
        SettingsStore.Load();

        var provider = SettingsStore.Provider
            ?? Environment.GetEnvironmentVariable("AI_PROVIDER")
            ?? Configuration.Providers.Keys.FirstOrDefault()
            ?? "ollama";
        if (Configuration.Providers.ContainsKey(provider))
        {
            Configuration.SetProvider(provider);
            var model = SettingsStore.Model
                ?? Environment.GetEnvironmentVariable("AI_MODEL")
                ?? Configuration.Providers[provider].DefaultModel;
            Configuration.SetModel(model);
        }

        await Configuration.RefreshContextWindowSizeAsync();

        // The UI must live on a dedicated STA thread: WebView2 requires STA,
        // and the continuation of the awaits above runs on an MTA thread-pool
        // thread, which would fail WebView2 startup (RPC_E_CHANGED_MODE).
        int exitCode = 0;
        var uiThread = new Thread(() =>
        {
            ApplicationConfiguration.Initialize();
            using var form = new MainForm();
            Application.Run(form);
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();
        return exitCode;
    }
}
