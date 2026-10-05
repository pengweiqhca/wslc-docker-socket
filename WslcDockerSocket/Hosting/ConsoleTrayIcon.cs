namespace WslcDockerSocket.Hosting;

using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Optional tray-icon launch mode, enabled with <c>--type=trayIcon</c>. The console window is hidden before
/// anything else runs instead of being minimized to the taskbar; the process keeps its console (so redirected
/// output, logging, and Ctrl+C all work exactly as in normal console mode), it's just not shown until the tray
/// icon is clicked.
/// </summary>
/// <remarks>
/// Minimize-button hooking (the previous approach) breaks under Windows Terminal: <c>GetConsoleWindow()</c>
/// there returns ConPTY's hidden proxy window, not the real Terminal window the user sees, so the minimize
/// click never reaches it. Direct <c>ShowWindow</c> calls against that same handle do not have this problem —
/// Windows Terminal forwards them to the real window as an explicit compatibility shim (see
/// https://github.com/microsoft/terminal/blob/main/doc/specs/%2312570%20-%20Show%20Hide%20operations%20on%20GetConsoleWindow%20via%20PTY.md).
/// Hiding and restoring the window programmatically therefore works under both the classic Console Host and
/// Windows Terminal, where hooking the button click did not.
///
/// One caveat inherited from that shim: hiding/restoring acts on the whole Terminal window, including any other
/// tabs sharing it, when the app is launched from an already-open Windows Terminal tab rather than its own
/// window.
/// </remarks>
internal sealed class ConsoleTrayIcon : IDisposable
{
    private const string TypeArgumentKey = "type";
    private const string TrayIconTypeValue = "trayIcon";

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private MessagePump? _pump;

    private ConsoleTrayIcon(IHostApplicationLifetime lifetime)
    {
        _thread = new Thread(() => RunMessageLoop(lifetime))
        {
            IsBackground = true,
            Name = "ConsoleTrayIcon",
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Whether <c>--type=trayIcon</c> was passed on the command line.</summary>
    public static bool IsRequested(string[] args) => string.Equals(
        new ConfigurationBuilder().AddCommandLine(args).Build()[TypeArgumentKey],
        TrayIconTypeValue,
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hides the console window as early as possible in tray-icon mode, before the rest of startup (host
    /// building, Hyper-V adapter detection, etc.) has a chance to run and keep it visible for longer than
    /// necessary. There's no console window to hide when <c>GetConsoleWindow()</c> returns none, e.g. when the
    /// process was launched detached from any console.
    /// </summary>
    public static void HideConsoleWindow()
    {
        var consoleWindow = NativeMethods.GetConsoleWindow();
        if (consoleWindow != IntPtr.Zero) NativeMethods.ShowWindow(consoleWindow, NativeMethods.SwHide);
    }

    /// <summary>Starts the tray icon, or returns null when there is no console window to show/hide.</summary>
    public static ConsoleTrayIcon? Start(IHostApplicationLifetime lifetime)
    {
        if (NativeMethods.GetConsoleWindow() == IntPtr.Zero) return null;

        var trayIcon = new ConsoleTrayIcon(lifetime);
        trayIcon._thread.Start();
        trayIcon._ready.Wait();
        return trayIcon;
    }

    private void RunMessageLoop(IHostApplicationLifetime lifetime)
    {
        _pump = new MessagePump(lifetime);
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
    /// A never-shown control whose only purpose is owning a window handle: <see cref="NotifyIcon"/> needs a
    /// message loop to dispatch to, and <see cref="Control.BeginInvoke(Delegate)"/> gives
    /// <see cref="ConsoleTrayIcon.Dispose"/> a safe way to reach this thread from the outside.
    /// </summary>
    private sealed class MessagePump : Control
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Icon _trayIcon;

        public MessagePump(IHostApplicationLifetime lifetime)
        {
            // Forces the window handle to be created now, without ever showing anything on screen.
            _ = Handle;

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show console", null, (_, _) => RestoreConsole());
            menu.Items.Add("Exit", null, (_, _) => lifetime.StopApplication());

            _trayIcon = EmojiIcon.Render("\U0001F6A2"); // 🚢 — evokes "container" without borrowing anyone's logo.
            _notifyIcon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "wslc-docker-socket",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) RestoreConsole();
            };
        }

        private static void RestoreConsole()
        {
            var consoleWindow = NativeMethods.GetConsoleWindow();
            if (consoleWindow == IntPtr.Zero) return;

            NativeMethods.ShowWindow(consoleWindow, NativeMethods.SwRestore);
            NativeMethods.SetForegroundWindow(consoleWindow);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _trayIcon.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
