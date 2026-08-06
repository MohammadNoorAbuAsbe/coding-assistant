using System.Diagnostics;

namespace TerminalAiAssistant;

/// <summary>
/// Produces concrete improvement tasks for autopilot cycles. Weak models
/// suffer decision paralysis when told to "pick an improvement" — they
/// explore forever. Scanning for real TODO/FIXME/HACK markers gives them
/// actual work to implement instead of open-ended choices.
/// </summary>
internal static class AutopilotSuggestions
{
    private static readonly string[] ScanPatterns = { "TODO", "FIXME", "HACK" };

    internal static string BuildDirective()
    {
        List<string> tasks = ScanForTasks();
        if (tasks.Count == 0)
        {
            tasks =
            [
                "Improve the CLI UI/UX: polish the startup output, menus, colors, or streaming display (Program.cs, ChatOrchestrator.cs) so the tool feels better to use",
                "Improve performance: find a hot path — token estimation runs on every tool result, context compaction is heavy — and make it faster or cheaper",
                "Improve an algorithm: fuzzy line matching in PatchHandler.cs, token estimation in TokenEstimator.cs, or stall detection in ChatOrchestrator.cs",
                "Implement a useful feature: a new slash command, tool, or convenience behavior a user would notice",
                "Fix a real bug you noticed while reading the code (you spotted one in BuildVerifier.cs earlier — verify and fix it)"
            ];
        }

        string list = string.Join("\n", tasks.Take(5).Select(t => $"  - {t}"));
        return "<AUTOPILOT DIRECTIVE> You have explored for many tool calls without changing any file. Stop exploring entirely — you already know enough to act. Pick the HIGHEST-VALUE task you can complete — a change a human user would notice and appreciate (UI/UX, performance, algorithm, feature, real bug) — and implement it. Your very next message MUST be a single Edit, ApplyPatch, or Write tool call that implements it:\n"
            + list
            + "\nPick the first one you can complete. Do NOT call Read, Grep, Glob, or any other tool before the edit. After the edit, end your turn with a one-paragraph summary.";
    }

    internal static string BuildNoChangeCarryover()
    {
        return "LAST CYCLE RESULT: the previous cycle made NO file changes. This cycle you MUST make at least one file change. Spend at most 10 tool calls exploring before your first Edit/ApplyPatch/Write call.";
    }

    private static List<string> ScanForTasks()
    {
        string? rgPath = RipgrepHelper.FindRipgrep();
        if (string.IsNullOrEmpty(rgPath))
            return [];

        var results = new List<string>();
        foreach (string pattern in ScanPatterns)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = rgPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Environment.CurrentDirectory
                };
                psi.ArgumentList.Add("--max-count");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add(pattern);
                psi.ArgumentList.Add("--glob");
                psi.ArgumentList.Add("!bin");
                psi.ArgumentList.Add("--glob");
                psi.ArgumentList.Add("!obj");
                psi.ArgumentList.Add(Environment.CurrentDirectory);

                using var proc = Process.Start(psi);
                if (proc == null) continue;
                string stdout = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(3000))
                {
                    try { proc.Kill(); } catch { }
                }

                foreach (string line in stdout.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r', '\n').Trim();
                    if (trimmed.Length == 0) continue;
                    results.Add($"{pattern}: {MakeRelative(trimmed)}");
                    if (results.Count >= 6) return results;
                }
            }
            catch
            {
                // ripgrep unavailable or failed — fall back to generic tasks.
            }
        }
        return results;
    }

    private static string MakeRelative(string line)
    {
        int colon = line.IndexOf(':');
        if (colon <= 0) return line;

        string filePart = line[..colon];
        string root = Environment.CurrentDirectory;
        if (filePart.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string rel = filePart[root.Length..].TrimStart('\\', '/');
            return rel + line[colon..];
        }
        return line;
    }
}
