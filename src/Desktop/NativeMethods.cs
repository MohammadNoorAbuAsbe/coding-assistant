using System.Runtime.InteropServices;

namespace TerminalAiAssistant.Desktop;

/// <summary>
/// Native interop for the borderless desktop window: rounded corners and
/// native resize/shadow behavior via the classic WS_THICKFRAME hybrid.
/// </summary>
internal static class NativeMethods
{
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCACTIVATE = 0x0086;
    public const int WM_NCCALCSIZE = 0x0083;

    public const int HTCAPTION = 2;
    public const int HTCLIENT = 1;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public System.Drawing.Point ptReserved;
        public System.Drawing.Point ptMaxSize;
        public System.Drawing.Point ptMaxPosition;
        public System.Drawing.Point ptMinTrackSize;
        public System.Drawing.Point ptMaxTrackSize;
    }

    public static void ApplyCornerPreference(IntPtr hwnd, bool round)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;

        try
        {
            int pref = round ? DWMWCP_ROUND : 1;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch
        {
            // Older Windows builds ignore the attribute.
        }
    }

    public static void ApplyDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;

        try
        {
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // Optional.
        }
    }
}
