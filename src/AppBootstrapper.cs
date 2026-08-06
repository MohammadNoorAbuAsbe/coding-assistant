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

    public sealed class CancelController
    {
        private CancellationTokenSource _cts = new();
        private bool _cancelPressed;

        public CancellationToken Token => _cts.Token;

        /// <summary>
        /// True once Ctrl+C has been pressed at least once. Autopilot mode
        /// checks this between cycles to decide whether to keep going.
        /// </summary>
        public bool StopRequested => _cancelPressed;

        public void Arm()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _cancelPressed = false;
        }

        public void RegisterCancelHandler()
        {
            Console.CancelKeyPress += (_, e) =>
            {
                if (_cancelPressed)
                    return;

                e.Cancel = true;
                _cancelPressed = true;
                Console.Error.WriteLine("\n[Interrupted] Cancelling...");
                _cts.Cancel();
            };
        }
    }
}
