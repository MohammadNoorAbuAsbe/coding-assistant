using System;

namespace TerminalAiAssistant;

public enum UiTheme
{
    Cyberpunk,
    Matrix,
    Nord,
    Dracula,
    Neon,
    Sunset,
    Minimalist
}

public static class ThemeManager
{
    private static UiTheme _currentTheme = UiTheme.Cyberpunk;

    public static UiTheme CurrentTheme
    {
        get => _currentTheme;
        set => _currentTheme = value;
    }

    public static void LoadThemeFromEnvironment()
    {
        var env = Environment.GetEnvironmentVariable("AI_UI_THEME");
        if (!string.IsNullOrEmpty(env) && Enum.TryParse<UiTheme>(env, true, out var parsed))
        {
            _currentTheme = parsed;
        }
    }

    public static ConsoleColor Primary => _currentTheme switch
    {
        UiTheme.Cyberpunk => ConsoleColor.Cyan,
        UiTheme.Matrix => ConsoleColor.Green,
        UiTheme.Nord => ConsoleColor.Blue,
        UiTheme.Dracula => ConsoleColor.Magenta,
        UiTheme.Neon => ConsoleColor.Yellow,
        UiTheme.Sunset => ConsoleColor.DarkYellow,
        UiTheme.Minimalist => ConsoleColor.White,
        _ => ConsoleColor.Cyan
    };

    public static ConsoleColor Secondary => _currentTheme switch
    {
        UiTheme.Cyberpunk => ConsoleColor.Magenta,
        UiTheme.Matrix => ConsoleColor.DarkGreen,
        UiTheme.Nord => ConsoleColor.Cyan,
        UiTheme.Dracula => ConsoleColor.DarkMagenta,
        UiTheme.Neon => ConsoleColor.Cyan,
        UiTheme.Sunset => ConsoleColor.Red,
        UiTheme.Minimalist => ConsoleColor.DarkGray,
        _ => ConsoleColor.Magenta
    };

    public static ConsoleColor Accent => _currentTheme switch
    {
        UiTheme.Cyberpunk => ConsoleColor.Yellow,
        UiTheme.Matrix => ConsoleColor.White,
        UiTheme.Nord => ConsoleColor.Green,
        UiTheme.Dracula => ConsoleColor.Cyan,
        UiTheme.Neon => ConsoleColor.Green,
        UiTheme.Sunset => ConsoleColor.Yellow,
        UiTheme.Minimalist => ConsoleColor.Gray,
        _ => ConsoleColor.Yellow
    };

    public static ConsoleColor BorderColor => _currentTheme switch
    {
        UiTheme.Cyberpunk => ConsoleColor.DarkCyan,
        UiTheme.Matrix => ConsoleColor.DarkGreen,
        UiTheme.Nord => ConsoleColor.DarkBlue,
        UiTheme.Dracula => ConsoleColor.DarkMagenta,
        UiTheme.Neon => ConsoleColor.DarkYellow,
        UiTheme.Sunset => ConsoleColor.DarkRed,
        UiTheme.Minimalist => ConsoleColor.DarkGray,
        _ => ConsoleColor.DarkCyan
    };

    public static ConsoleColor MutedText => ConsoleColor.DarkGray;

    public static string ThemeName => _currentTheme switch
    {
        UiTheme.Cyberpunk => "Cyberpunk Neon",
        UiTheme.Matrix => "Matrix Terminal",
        UiTheme.Nord => "Nordic Frost",
        UiTheme.Dracula => "Dracula Night",
        UiTheme.Neon => "Cyber Neon",
        UiTheme.Sunset => "Solar Sunset",
        UiTheme.Minimalist => "Clean Minimalist",
        _ => "Cyberpunk Neon"
    };

    public static void CycleTheme()
    {
        var values = (UiTheme[])Enum.GetValues(typeof(UiTheme));
        int next = (((int)_currentTheme + 1) % values.Length);
        _currentTheme = values[next];
    }
}
