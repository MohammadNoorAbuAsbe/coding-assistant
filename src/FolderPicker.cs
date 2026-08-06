using System.Windows.Forms;

namespace TerminalAiAssistant;

public static class FolderPicker
{
    /// <summary>
    /// Shows the native Windows "Select Folder" dialog and returns the chosen
    /// path, or null when the user cancels. The dialog is shown on a dedicated
    /// STA thread because COM dialogs require a single-threaded apartment while
    /// console apps run in MTA. Returns null on non-Windows platforms.
    /// </summary>
    public static string? PickFolder(string initialDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Select the working folder for the AI assistant",
                    SelectedPath = initialDirectory,
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    AutoUpgradeEnabled = true,
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                    result = dialog.SelectedPath;
            }
            catch
            {
                // Fall back to the current directory if the dialog cannot be shown
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    public static bool ShouldSkip()
    {
        var value = Environment.GetEnvironmentVariable("SKIP_FOLDER_PICKER");
        return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
