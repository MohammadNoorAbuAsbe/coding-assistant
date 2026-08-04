using OpenAI.Chat;
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
        UndoJournal.Clear();
        using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
            await Console.Error.WriteLineAsync("Session reset.");
        continue;
    }

    if (prompt.Equals("/undo", StringComparison.OrdinalIgnoreCase))
    {
        var entry = UndoJournal.UndoLast();
        if (entry == null)
        {
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                await Console.Error.WriteLineAsync("Nothing to undo — no file changes have been recorded this session.");
            continue;
        }

        string action = entry.ExistedBefore ? "Restored" : "Deleted";
        using (ConsoleStyler.WithColor(entry.ExistedBefore ? ConsoleColor.Green : ConsoleColor.Yellow))
            await Console.Error.WriteLineAsync($"{action} {entry.FullPath} (was {entry.ToolName}, {entry.Timestamp:HH:mm:ss}).");

        if (session.SessionStarted)
        {
            session.Messages.Add(new UserChatMessage($"The user manually undid the most recent file modification via /undo: {entry.FullPath} was {(entry.ExistedBefore ? "restored to its previous content" : "deleted (it did not exist before the tool call was made)")}. The tool call that made this change was {entry.ToolName}. Do not rely on the previous tool results for this file — re-read it if you need its current state."));
        }
        continue;
    }

    if (prompt.Equals("/history", StringComparison.OrdinalIgnoreCase))
    {
        var entries = UndoJournal.List();
        if (entries.Count == 0)
        {
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                await Console.Error.WriteLineAsync("No file changes recorded this session.");
            continue;
        }

        using (ConsoleStyler.WithColor(ConsoleColor.Blue))
            await Console.Error.WriteLineAsync($"Undo history ({entries.Count} change(s), newest first):");
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            using (ConsoleStyler.WithColor(e.ExistedBefore ? ConsoleColor.Green : ConsoleColor.Yellow))
                await Console.Error.WriteLineAsync($"  #{i + 1} {e.FullPath} — {e.ToolName} — {e.Timestamp:HH:mm:ss} {(e.ExistedBefore ? "" : "(created)")}");
        }
        continue;
    }

    await ChatOrchestrator.Run(session, prompt, cts.Token);
}
