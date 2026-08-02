using TerminalAiAssistant;

Configuration.LoadEnvFile();
Configuration.LoadProviderConfigs();

var providers = Configuration.Providers;

var providerId = AppBootstrapper.ResolveProviderId(providers);
Configuration.SetProvider(providerId);

var model = AppBootstrapper.ResolveModel(providerId, providers[providerId]);
Configuration.SetModel(model);

await Configuration.RefreshContextWindowSizeAsync();

var session = new ChatSession();

using var cts = new CancellationTokenSource();
AppBootstrapper.SetupCancelHandler(cts);

while (true)
{
    var prompt = MenuHandler.GetPrompt();
    if (string.IsNullOrWhiteSpace(prompt))
        continue;

    if (prompt.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
        prompt.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        break;

    if (prompt.Equals("/new", StringComparison.OrdinalIgnoreCase) ||
        prompt.Equals("/reset", StringComparison.OrdinalIgnoreCase))
    {
        session.Reset();
        using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
            await Console.Error.WriteLineAsync("Session reset.");
        continue;
    }

    await ChatOrchestrator.Run(session, prompt, cts.Token);
}
