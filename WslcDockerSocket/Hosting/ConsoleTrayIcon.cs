namespace WslcDockerSocket.Hosting;

using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// Hides the console window into the system tray instead of the taskbar when it's minimized. Console input,
/// output, and the rest of the process are untouched by this: it only owns a tray icon and a dedicated STA
/// thread to pump the Windows messages <see cref="NotifyIcon"/> and the minimize hook need.
/// </summary>
/// <remarks>
/// Detecting the minimize click relies on <c>GetConsoleWindow</c>, which under Windows Terminal returns the
/// hidden ConPTY pseudo-console window rather than the visible frame the user actually minimizes. There the
/// tray icon still appears and can restore the window, but clicking the minimize button itself won't trigger
/// it. Running under the classic Console Host (conhost.exe, e.g. cmd.exe/PowerShell's default host) is
/// unaffected.
/// </remarks>
internal sealed class ConsoleTrayIcon : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private MessagePump? _pump;

    private ConsoleTrayIcon(IntPtr consoleWindow, IHostApplicationLifetime lifetime)
    {
        _thread = new Thread(() => RunMessageLoop(consoleWindow, lifetime))
        {
            IsBackground = true,
            Name = "ConsoleTrayIcon",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Starts the tray icon, or returns null when there is no console window to minimize.</summary>
    public static ConsoleTrayIcon? Start(IHostApplicationLifetime lifetime)
    {
        var consoleWindow = NativeMethods.GetConsoleWindow();
        if (consoleWindow == IntPtr.Zero) return null;

        var trayIcon = new ConsoleTrayIcon(consoleWindow, lifetime);
        trayIcon._thread.Start();
        trayIcon._ready.Wait();
        return trayIcon;
    }

    private void RunMessageLoop(IntPtr consoleWindow, IHostApplicationLifetime lifetime)
    {
        _pump = new MessagePump(consoleWindow, lifetime);
        _ready.Set();
        Application.Run();
    }

    public void Dispose()
    {
        _ready.Dispose();

        var pump = _pump;
        if (pump is null || pump.IsDisposed) return;

        // ApplicationStopping can fire on this very tray thread (the "Exit" menu item calls
        // IHostApplicationLifetime.StopApplication() directly), so this must never block waiting for the tray
        // thread to finish; BeginInvoke queues the cleanup instead of running it inline.
        pump.BeginInvoke(new Action(() =>
        {
            pump.Dispose();
            Application.ExitThread();
        }));
    }

    /// <summary>
    /// A never-shown control whose only purpose is owning a window handle: <see cref="NotifyIcon"/> and
    /// <c>SetWinEventHook</c> both need a message loop to dispatch to, and <see cref="Control.BeginInvoke(Delegate)"/>
    /// gives <see cref="ConsoleTrayIcon.Dispose"/> a safe way to reach this thread from the outside.
    /// </summary>
    private sealed class MessagePump : Control
    {
        private readonly IntPtr _consoleWindow;
        private readonly NotifyIcon _notifyIcon;
        private readonly NativeMethods.WinEventDelegate _winEventCallback;
        private readonly IntPtr _winEventHook;

        public MessagePump(IntPtr consoleWindow, IHostApplicationLifetime lifetime)
        {
            _consoleWindow = consoleWindow;

            // Forces the window handle to be created now, without ever showing anything on screen.
            _ = Handle;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show console", null, (_, _) => RestoreConsole());
            menu.Items.Add("Exit", null, (_, _) => lifetime.StopApplication());

            _notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "wslc-docker-socket",
                ContextMenuStrip = menu,
                Visible = false,
            };
            _notifyIcon.DoubleClick += (_, _) => RestoreConsole();

            // Scoped to all processes/threads (idProcess=0, idThread=0): the console window belongs to
            // conhost/Terminal, not this process, so the hook cannot be scoped to this process's own id. The
            // callback below filters events down to the console window handle itself.
            _winEventCallback = OnWinEvent;
            _winEventHook = NativeMethods.SetWinEventHook(NativeMethods.EventSystemMinimizeStart,
                NativeMethods.EventSystemMinimizeStart, IntPtr.Zero, _winEventCallback, 0, 0,
                NativeMethods.WinEventOutOfContext);
        }

        private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
            uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd != _consoleWindow || idObject != NativeMethods.ObjIdWindow || idChild != NativeMethods.ChildIdSelf)
            {
                return;
            }

            NativeMethods.ShowWindow(_consoleWindow, NativeMethods.SwHide);
            _notifyIcon.Visible = true;
        }

        private void RestoreConsole()
        {
            _notifyIcon.Visible = false;
            NativeMethods.ShowWindow(_consoleWindow, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(_consoleWindow);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_winEventHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_winEventHook);

                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
