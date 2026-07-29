using TerminalAiAssistant;

Configuration.LoadEnvFile();
Configuration.LoadProviderConfigs();

var providers = Configuration.Providers;

while (true)
{
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

    var prompt = MenuHandler.GetPrompt();
    if (string.IsNullOrWhiteSpace(prompt))
        continue;

    await ChatOrchestrator.Run(prompt);
}
