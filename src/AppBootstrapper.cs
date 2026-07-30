namespace TerminalAiAssistant;

public static class AppBootstrapper
{
    public static string ResolveProviderId(Dictionary<string, ProviderConfig> providers)
    {
        var envProvider = Environment.GetEnvironmentVariable("AI_PROVIDER");
        return envProvider != null && providers.ContainsKey(envProvider)
            ? envProvider
            : MenuHandler.SelectProvider(providers);
    }

    public static string ResolveModel(string providerId, ProviderConfig providerConfig)
    {
        var envProvider = Environment.GetEnvironmentVariable("AI_PROVIDER");
        var envModel = Environment.GetEnvironmentVariable("AI_MODEL");
        return envProvider != null && envModel != null
            ? envModel
            : MenuHandler.SelectModel(providerId, providerConfig);
    }

    public static void SetupCancelHandler(CancellationTokenSource cts)
    {
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
    }
}
