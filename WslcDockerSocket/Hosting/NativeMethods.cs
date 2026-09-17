namespace WslcDockerSocket.Hosting;

using System.Runtime.InteropServices;

/// <summary>Win32 calls backing the console-to-tray minimize behavior in <see cref="ConsoleTrayIcon"/>.</summary>
internal static class NativeMethods
{
    public const uint EventSystemMinimizeStart = 0x0016;
    public const uint WinEventOutOfContext = 0x0000;
    public const int SwHide = 0;
    public const int SwRestore = 9;
    public const int ObjIdWindow = 0;
    public const int ChildIdSelf = 0;

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject,
        int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Hooked with hwnd 0/thread 0 (all processes) because the console window belongs to conhost, not this
    /// process, so <c>SetWinEventHook</c> cannot be scoped to this process's own id. The callback filters events
    /// down to the console window handle itself.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
