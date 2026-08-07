namespace TerminalAiAssistant;

/// <summary>
/// Autonomous improvement mode. Drives the normal agent loop in an infinite
/// cycle: after each completed improvement, the agent is prompted to pick the
/// next one and continue. It never stops and never asks the user — Question
/// tool calls are auto-answered telling it to decide for itself. Stopped only
/// by the user (Ctrl+C).
/// </summary>
public static class Autopilot
{
    private static bool _isActive;

    /// <summary>
    /// True while an autopilot session is running. Other components use this
    /// to switch behavior (Question auto-answer, verify disabled, ...).
    /// </summary>
    public static bool IsActive => _isActive;

    internal static void SetActiveForTesting(bool active) => _isActive = active;

    private const string MissionPrompt = @"AUTONOMOUS IMPROVEMENT MISSION

You are operating in autonomous mode. Your mission: continuously improve this project — the codebase you are running from. You will keep working indefinitely until the user stops you.

Rules:
- One improvement at a time. Pick the next highest-value improvement, implement it completely with real tool calls (Read/Edit/ApplyPatch/Write), summarize what you changed, then immediately pick the next improvement.
- ACT FAST: do not hunt for the perfect improvement. Spend at most 10 tool calls exploring before your first file change — the first plausible improvement is the right one. Any correct change is better than no change.
- Explore freely and as much as you need before deciding what to change: read, search, and inspect the codebase until you understand it. Exploration and planning are part of the work — there is no time limit on them.
- NEVER stop on your own. There is no end state — after finishing an improvement, continue with the next one without waiting for anything.
- NEVER ask the user. You cannot receive user input. If you want to ask a question or need a decision, decide yourself based on what produces the best project, and continue.
- This codebase is your own source code. Your changes only take effect when the process restarts — the running process executes the old code, which is expected and fine. Never try to restart the process and never try to test the changes you make.
- NEVER run builds or tests (dotnet build/run/test, or any build/test command). The running process locks the output files, so they fail with file-lock errors regardless of correctness. Reason about correctness by reading carefully instead.
- Keep the codebase coherent: follow the existing code style and conventions, prefer small focused changes, keep the project in a state that will compile when the process is restarted.
- PREFER HIGH-VALUE improvements, ranked in order: (1) user-visible UI/UX polish, (2) performance wins, (3) algorithmic improvements, (4) useful new features, (5) real bug fixes, (6) clarity/refactoring. Avoid busywork: documentation-only or test-only changes are lowest priority — only do them if nothing better is available. Aim for changes a human user would notice and appreciate.
- You may improve anything: bugs, features, performance, clarity, tests, documentation, configuration, tooling.
- If an improvement is blocked, abandon it and pick a different one. Never get stuck on a single problem.
- When you finish an improvement, end with a one-paragraph summary of what you changed and what you will do next.";

    public static async Task Run(ChatSession session, AppBootstrapper.CancelController cancelController)
    {
        _isActive = true;
        int cycle = 0;
        bool forceChangeNextCycle = false;
        try
        {
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                await Console.Error.WriteLineAsync("[Autopilot] Autonomous mode started. The agent will keep improving the project until you press Ctrl+C.");
            using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                await Console.Error.WriteLineAsync("[Autopilot] Changes apply to the source on disk; the running process keeps executing the old code until you restart.");
            AppUi.Send("autopilot", new { active = true, cycle = 0, message = "Autonomous mode started — the agent keeps improving the project until stopped." });

            while (true)
            {
                if (cancelController.StopRequested)
                    break;

                cycle++;
                using (ConsoleStyler.WithColor(ConsoleColor.Cyan))
                    await Console.Error.WriteLineAsync($"\n================ Autopilot cycle {cycle} ================");
                AppUi.Send("autopilot", new { active = true, cycle, message = $"Autopilot cycle {cycle}" });

                int journalCount = session.Undo.List().Count;
                string prompt = forceChangeNextCycle
                    ? MissionPrompt + "\n\n" + AutopilotSuggestions.BuildNoChangeCarryover()
                    : MissionPrompt;
                await ChatOrchestrator.Run(session, prompt, cancelController.Token);

                if (cancelController.StopRequested)
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                        await Console.Error.WriteLineAsync("\n[Autopilot] Stopped by user.");
                    AppUi.Send("autopilot", new { active = false, cycle, message = "Autopilot stopped by user." });
                    break;
                }

                var changes = session.Undo.List().Take(Math.Max(0, session.Undo.List().Count - journalCount)).ToList();
                forceChangeNextCycle = changes.Count == 0;
                if (changes.Count == 0)
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                        await Console.Error.WriteLineAsync($"[Autopilot] Cycle {cycle} complete — no files changed. Next cycle will be forced to make a change.");
                }
                else
                {
                    using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
                        await Console.Error.WriteLineAsync($"[Autopilot] Cycle {cycle} complete — {changes.Count} change(s):");
                    foreach (var entry in changes)
                    {
                        using (ConsoleStyler.WithColor(entry.ExistedBefore ? ConsoleColor.Green : ConsoleColor.Yellow))
                            await Console.Error.WriteLineAsync($"  - {entry.FullPath} ({entry.ToolName})");
                    }
                }
            }
        }
        finally
        {
            _isActive = false;
        }

        using (ConsoleStyler.WithColor(ConsoleColor.DarkGray))
            await Console.Error.WriteLineAsync($"[Autopilot] Session ended after {cycle} cycle(s). Changes are on disk — restart the app to run them.");
        AppUi.Send("autopilot", new { active = false, cycle, message = $"Autopilot ended after {cycle} cycle(s)." });
    }
}
