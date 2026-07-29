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

while (true)
{
    var prompt = MenuHandler.GetPrompt();
    if (string.IsNullOrWhiteSpace(prompt))
        continue;

    if (prompt.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
        prompt.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        break;

    await ChatOrchestrator.Run(prompt);
}
