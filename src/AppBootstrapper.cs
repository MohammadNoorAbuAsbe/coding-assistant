namespace TerminalAiAssistant;

public static class AppBootstrapper
{
    public sealed class CancelController
    {
        private CancellationTokenSource _cts = new();
        private bool _cancelPressed;

        public CancellationToken Token => _cts.Token;

        /// <summary>
        /// True once a stop has been requested at least once. Autopilot mode
        /// checks this between cycles to decide whether to keep going.
        /// </summary>
        public bool StopRequested => _cancelPressed;

        public void Arm()
        {
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _cancelPressed = false;
        }

        /// <summary>Programmatically cancels the current operation (used by the desktop UI).</summary>
        public void Cancel()
        {
            _cancelPressed = true;
            _cts.Cancel();
        }
    }
}
