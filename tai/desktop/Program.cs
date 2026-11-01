using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TAI.Desktop
{
    /// <summary>The two independent windows this exe can host. Each mode is its own single instance.</summary>
    internal enum HostMode
    {
        /// <summary>Default. Standalone configuration / diagnostics console at "/".</summary>
        Console,
        /// <summary>"--assistant": full engineering workspace at "/workspace", optionally owned by TIA Portal.</summary>
        Assistant
    }

    /// <summary>Per-mode constants: title, mutex, start page.</summary>
    internal static class HostModes
    {
        public const string AssistantTitle = "TAI 工程助手";
        public const string ConsoleTitle = "TAI 配置与诊断";

        public static string Title(HostMode mode)
        {
            return mode == HostMode.Assistant ? AssistantTitle : ConsoleTitle;
        }

        /// <summary>Separate mutexes so the assistant and the console can be open at the same time.</summary>
        public static string MutexName(HostMode mode)
        {
            return mode == HostMode.Assistant ? @"Local\TAI.Desktop.Assistant" : @"Local\TAI.Desktop.Console";
        }

        public static string StartUrl(HostMode mode, bool embedded = false)
        {
            if (embedded) return ServerLauncher.BaseUrl + "assistant";
            return mode == HostMode.Assistant ? ServerLauncher.BaseUrl + "workspace" : ServerLauncher.BaseUrl;
        }
    }

    /// <summary>Command line options. All optional.</summary>
    internal sealed class HostOptions
    {
        /// <summary>Install root: contains TAI.Desktop.exe, runtime/, app/, mcp/.</summary>
        public string Root = AppDomain.CurrentDomain.BaseDirectory;
        /// <summary>True when --root was given (it is then forwarded to a console started from the assistant).</summary>
        public bool RootOverridden;
        /// <summary>PID of the TIA Portal process whose main window should own this window.</summary>
        public int OwnerPid;
        public bool DevTools;
        public HostMode Mode = HostMode.Console;
        /// <summary>"--embedded --owner PID": WS_CHILD panel + original-icon toggle inside that TIA main window.</summary>
        public bool Embedded;
        /// <summary>"--watch": tray integration watcher that starts one embedded helper per TIA Portal.</summary>
        public bool Watch;
        /// <summary>"--stop-integration": signals the watcher and all embedded helpers to close (TIA untouched).</summary>
        public bool StopIntegration;

        public static HostOptions Parse(string[] args)
        {
            var o = new HostOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string value = null;
                int eq = a.IndexOf('=');
                string key = eq > 0 ? a.Substring(0, eq) : a;
                if (eq > 0) value = a.Substring(eq + 1);
                Func<string> next = () => value ?? (i + 1 < args.Length ? args[++i] : null);

                switch (key.ToLowerInvariant())
                {
                    case "--assistant":
                        o.Mode = HostMode.Assistant;
                        break;
                    case "--console":
                        o.Mode = HostMode.Console;
                        break;
                    case "--owner":
                        int pid;
                        if (int.TryParse(next(), out pid) && pid > 0) o.OwnerPid = pid;
                        break;
                    case "--root":
                        string r = next();
                        if (r != null) r = r.Trim().Trim('"');
                        if (!string.IsNullOrWhiteSpace(r))
                        {
                            o.Root = Path.GetFullPath(r);
                            o.RootOverridden = true;
                        }
                        break;
                    case "--devtools":
                        o.DevTools = true;
                        break;
                    case "--embedded":
                        o.Embedded = true;
                        o.Mode = HostMode.Assistant;
                        break;
                    case "--watch":
                        o.Watch = true;
                        break;
                    case "--stop-integration":
                        o.StopIntegration = true;
                        break;
                }
            }
            return o;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var options = HostOptions.Parse(args);

            // No HWND is created by these calls; the embedded path must still be able to set the thread DPI
            // context before its first window.
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => HostLog.Write("UI exception: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => HostLog.Write("Unhandled: " + e.ExceptionObject);

            if (options.StopIntegration)
            {
                HostLog.Write("TAI.Desktop --stop-integration");
                IntegrationWatcher.Stop();
                return 0;
            }
            if (options.Watch)
            {
                HostLog.Write("TAI.Desktop --watch, root=" + options.Root);
                IntegrationWatcher.Run(options.Root);
                return 0;
            }
            if (options.Embedded)
            {
                HostLog.Write("TAI.Desktop --embedded, owner=" + options.OwnerPid + ", root=" + options.Root);
                // No owner-window / top-level fallback: a failed embed simply exits.
                return EmbeddedHost.Run(options);
            }

            bool created;
            using (var mutex = new Mutex(true, HostModes.MutexName(options.Mode), out created))
            {
                if (!created)
                {
                    // A window of the SAME mode is already open in this session: bring exactly that one forward.
                    ActivateExistingInstance(options.Mode);
                    return 0;
                }

                HostLog.Write("TAI.Desktop start, mode=" + options.Mode + ", root=" + options.Root + ", owner=" + options.OwnerPid);

                IntPtr owner = options.OwnerPid > 0 ? OwnerWindow.Resolve(options.OwnerPid) : IntPtr.Zero;
                var form = new MainForm(options);
                form.PlaceInitialWindow(owner);
                if (owner != IntPtr.Zero)
                {
                    // Ownership only (stays above TIA, minimizes with it). This is NOT a docked TIA panel.
                    form.Show(new OwnerWindow(owner));
                    HostLog.Write("Owned by window 0x" + owner.ToInt64().ToString("X"));
                }
                Application.Run(form);
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        private static void ActivateExistingInstance(HostMode mode)
        {
            try
            {
                // The first instance may still be starting (mutex taken, window not created yet): retry briefly.
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    IntPtr h = WindowMarker.Find(mode);
                    if (h != IntPtr.Zero)
                    {
                        WindowMarker.Activate(h);
                        return;
                    }
                    Thread.Sleep(150);
                }
                HostLog.Write("Existing " + mode + " instance holds the mutex but its window was not found.");
            }
            catch (Exception ex)
            {
                HostLog.Write("Activate existing instance failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Tags each top-level TAI window with a window property naming its mode, so activation targets
    /// the correct window instead of an arbitrary window of the same exe.
    /// </summary>
    internal static class WindowMarker
    {
        private const string PropName = "TAI.Desktop.HostMode";

        private static int Value(HostMode mode)
        {
            return mode == HostMode.Assistant ? 1 : 2;
        }

        public static void Mark(IntPtr hwnd, HostMode mode)
        {
            if (!NativeMethods.SetProp(hwnd, PropName, new IntPtr(Value(mode))))
                HostLog.Write("SetProp failed: " + Marshal.GetLastWin32Error());
        }

        public static void Unmark(IntPtr hwnd)
        {
            NativeMethods.RemoveProp(hwnd, PropName);
        }

        /// <summary>Finds the marked window of the given mode owned by another TAI.Desktop process in this session.</summary>
        public static IntPtr Find(HostMode mode)
        {
            int want = Value(mode);
            IntPtr found = IntPtr.Zero;
            string processName;
            int sessionId, selfId;
            using (var me = Process.GetCurrentProcess())
            {
                processName = me.ProcessName;
                sessionId = me.SessionId;
                selfId = me.Id;
            }

            NativeMethods.EnumWindowsProc callback = (h, l) =>
            {
                if (NativeMethods.GetProp(h, PropName).ToInt64() != want) return true;
                uint pid;
                NativeMethods.GetWindowThreadProcessId(h, out pid);
                if (pid == 0 || pid == selfId) return true;
                try
                {
                    using (var p = Process.GetProcessById((int)pid))
                    {
                        if (p.SessionId != sessionId ||
                            !string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch
                {
                    return true;
                }
                found = h;
                return false;
            };
            NativeMethods.EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return found;
        }

        public static void Activate(IntPtr h)
        {
            if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(h);
        }
    }

    /// <summary>Wraps a foreign HWND (TIA Portal main window) as a WinForms owner.</summary>
    internal sealed class OwnerWindow : IWin32Window
    {
        public OwnerWindow(IntPtr handle) { Handle = handle; }
        public IntPtr Handle { get; private set; }

        /// <summary>Returns the main window of the process, waiting briefly if it has none yet.</summary>
        public static IntPtr Resolve(int pid)
        {
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        p.Refresh();
                        if (p.HasExited) return IntPtr.Zero;
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && NativeMethods.IsWindow(h)) return h;
                        Thread.Sleep(150);
                    }
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("Owner process " + pid + " not usable: " + ex.Message);
            }
            return IntPtr.Zero;
        }
    }

    /// <summary>Physical-pixel scale factor of a monitor (process is PerMonitorV2 aware).</summary>
    internal static class DisplayScale
    {
        public static float ForWindow(IntPtr hwnd)
        {
            return ForMonitor(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST));
        }

        public static float ForPoint(System.Drawing.Point pt)
        {
            var p = new NativeMethods.POINT { X = pt.X, Y = pt.Y };
            return ForMonitor(NativeMethods.MonitorFromPoint(p, NativeMethods.MONITOR_DEFAULTTONEAREST));
        }

        private static float ForMonitor(IntPtr monitor)
        {
            try
            {
                uint dpiX, dpiY;
                if (monitor != IntPtr.Zero &&
                    NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0 &&
                    dpiX > 0)
                {
                    return dpiX / 96f;
                }
            }
            catch (Exception ex)
            {
                HostLog.Write("GetDpiForMonitor failed: " + ex.Message);
            }
            return 1f;
        }
    }

    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        public const uint MONITOR_DEFAULTTONEAREST = 2;
        public const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetProp(IntPtr hWnd, string lpString);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    }

    internal static class HostLog
    {
        private static readonly object Gate = new object();

        public static string Directory
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "TAI", "logs");
            }
        }

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(Path.Combine(Directory, "desktop.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + Process.GetCurrentProcess().Id + "] " + line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the UI.
            }
        }
    }
}
