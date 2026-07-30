using TerminalAiAssistant;

Configuration.LoadEnvFile();
Configuration.LoadProviderConfigs();

var providers = Configuration.Providers;

string providerId;
var envProvider = Environment.GetEnvironmentVariable("AI_PROVIDER");
if (envProvider != null && providers.ContainsKey(envProvider))
{
    providerId = envProvider;
}
else
{
    providerId = MenuHandler.SelectProvider(providers);
}

Configuration.SetProvider(providerId);
var providerConfig = providers[providerId];

string model;
var envModel = Environment.GetEnvironmentVariable("AI_MODEL");
if (envProvider != null && envModel != null)
{
    model = envModel;
}
else
{
    model = MenuHandler.SelectModel(providerId, providerConfig);
}

Configuration.SetModel(model);

var session = new ChatSession();

using var cts = new CancellationTokenSource();
var cancelPressed = false;

Console.CancelKeyPress += (sender, e) =>
{
    if (cancelPressed)
        return;

    e.Cancel = true;
    cancelPressed = true;
    Console.Error.WriteLine("\n[Interrupted] Cancelling...");
    cts.Cancel();
};

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
            Console.Error.WriteLine("Session reset.");
        continue;
    }

    await ChatOrchestrator.Run(session, prompt, cts.Token);
}
