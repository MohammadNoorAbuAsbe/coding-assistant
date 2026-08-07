using System;

namespace TerminalAiAssistant;

public static class NeuralSpinner
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] QuantumPulses = ["⚡", "🔮", "✨", "💫", "🚀", "💎", "🌐", "🌀"];
    private static int _frameIndex;

    public static void RenderThoughtHeader(int iteration, int? maxIterations)
    {
        int width = 76;
        try { width = Math.Max(40, Math.Min(120, Console.WindowWidth - 4)); } catch { }

        string iterStr = maxIterations == null ? $"Iteration {iteration}" : $"{iteration}/{maxIterations}";
        string pulse = QuantumPulses[(iteration - 1) % QuantumPulses.Length];
        string frame = Frames[_frameIndex++ % Frames.Length];

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  ╭" + new string('─', width - 2) + "╮");
            Console.Error.Write($"  │ ");
        }

        using (ConsoleStyler.WithColor(ThemeManager.Accent))
        {
            Console.Error.Write($"{frame} {pulse} 2026 NEURAL RESONANCE & THOUGHT STREAM ");
        }

        using (ConsoleStyler.WithColor(ThemeManager.MutedText))
        {
            Console.Error.Write($"[{iterStr}]");
        }

        int padding = width - 4 - 38 - iterStr.Length;
        if (padding > 0)
        {
            Console.Error.Write(new string(' ', padding));
        }

        using (ConsoleStyler.WithColor(ThemeManager.BorderColor))
        {
            Console.Error.WriteLine(" │");
            Console.Error.WriteLine($"  ╰" + new string('─', width - 2) + "╯");
        }
    }
}
