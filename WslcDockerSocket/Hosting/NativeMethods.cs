namespace WslcDockerSocket.Hosting;

using System.Runtime.InteropServices;

/// <summary>Win32 calls backing the tray-icon console mode in <see cref="ConsoleTrayIcon"/>.</summary>
internal static class NativeMethods
{
    public const int SwHide = 0;
    public const int SwRestore = 9;

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Releases the HICON produced by <see cref="Bitmap.GetHicon"/> in <see cref="EmojiIcon"/>.</summary>
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
}
