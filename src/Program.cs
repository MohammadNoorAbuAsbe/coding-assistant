using TerminalAiAssistant;

Configuration.LoadEnvFile();
Configuration.LoadProviderConfigs();

var providers = Configuration.Providers;

do
{
    string providerId;
    var envProvider = Environment.GetEnvironmentVariable("AI_PROVIDER");

    if (envProvider != null && providers.ContainsKey(envProvider))
    {
        providerId = envProvider;
        Console.WriteLine($"\nProvider: {providers[providerId].DisplayName} (from AI_PROVIDER)");
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
        Console.WriteLine($"Model: {model} (from AI_MODEL)");
    }
    else
    {
        model = MenuHandler.SelectModel(providerId, providerConfig);
    }

    Configuration.SetModel(model);

    var prompt = MenuHandler.GetPrompt();
    if (string.IsNullOrWhiteSpace(prompt))
    {
        Console.WriteLine("Prompt must not be empty.");
        continue;
    }

    await ChatOrchestrator.Run(prompt);

} while (MenuHandler.AskRunAgain());
