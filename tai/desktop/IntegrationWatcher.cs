using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TAI.Desktop
{
    /// <summary>
    /// Background TIA Portal integration lifecycle ("--watch").
    /// Owns the Local\TAI.Integration.Watcher mutex, a hidden ApplicationContext with a tray icon,
    /// and one embedded helper process per verified TIA Portal main window.
    /// It never kills TIA and never kills helpers: shutdown is cooperative through the
    /// manual-reset named event Local\TAI.Integration.Stop.
    /// </summary>
    internal static class IntegrationWatcher
    {
        public const string WatcherMutexName = @"Local\TAI.Integration.Watcher";
        public const string StopEventName = @"Local\TAI.Integration.Stop";

        private const int TickMs = 500;                 // stop-event poll cadence
        private const int ScanEveryTicks = 3;           // => portal scan every ~1.5 s
        private const int MinRelaunchSeconds = 10;      // min gap between launches for one TIA PID
        private const int BurstWindowSeconds = 300;     // restart budget window
        private const int MaxLaunchesPerWindow = 5;     // launches allowed per window, then back off
        private const int BackoffSeconds = 300;         // pause after budget exhausted
        private const int ShutdownWaitMs = 5000;        // total bounded wait for helpers to exit

        /// <summary>Runs the watcher on the calling (STA) thread until Exit, Stop() or the stop event.</summary>
        public static void Run(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) root = AppDomain.CurrentDomain.BaseDirectory;
            try { root = Path.GetFullPath(root); } catch { }

            bool created;
            using (var mutex = new Mutex(true, WatcherMutexName, out created))
            {
                if (!created)
                {
                    Log("Integration watcher already running in this session; exiting.");
                    return;
                }

                EventWaitHandle stopEvent = null;
                try
                {
                    bool eventCreated;
                    stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName, out eventCreated);
                    // Only the watcher resets the event, and only at startup.
                    stopEvent.Reset();
                    Log("Integration watcher start, root=" + root + ", stopEvent " + (eventCreated ? "created" : "opened+reset"));

                    using (var context = new WatcherContext(root, stopEvent))
                    {
                        Application.Run(context);
                    }
                }
                catch (Exception ex)
                {
                    Log("Integration watcher failed: " + ex);
                    // Make sure helpers we may have started do not outlive a crashed watcher silently.
                    try { if (stopEvent != null) stopEvent.Set(); } catch { }
                }
                finally
                {
                    if (stopEvent != null) stopEvent.Dispose();
                    Log("Integration watcher exit.");
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        /// <summary>
        /// Asks the watcher and every embedded helper (watcher-launched or manually launched) to stop
        /// by setting the shared named event. Never kills processes.
        /// </summary>
        public static void Stop()
        {
            EventWaitHandle ev;
            try
            {
                if (EventWaitHandle.TryOpenExisting(StopEventName, out ev))
                {
                    using (ev)
                    {
                        ev.Set();
                    }
                    Log("Stop requested: " + StopEventName + " set.");
                }
                else
                {
                    Log("Stop requested: no watcher/helper holds " + StopEventName + "; nothing to stop.");
                }
            }
            catch (Exception ex)
            {
                Log("Stop request failed: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------------------------------

        private sealed class HelperEntry
        {
            public int OwnerPid;
            public Process Child;
            public DateTime LastLaunchUtc = DateTime.MinValue;
            public DateTime BurstStartUtc = DateTime.MinValue;
            public int BurstCount;
            public DateTime BackoffUntilUtc = DateTime.MinValue;
        }

        private sealed class WatcherContext : ApplicationContext
        {
            private readonly string _root;
            private readonly EventWaitHandle _stopEvent;
            private readonly NotifyIcon _tray;
            private readonly ContextMenuStrip _menu;
            private readonly System.Windows.Forms.Timer _timer;
            private readonly Dictionary<int, HelperEntry> _helpers = new Dictionary<int, HelperEntry>();
            private int _tick;
            private bool _scanning;
            private bool _shuttingDown;

            public WatcherContext(string root, EventWaitHandle stopEvent)
            {
                _root = root;
                _stopEvent = stopEvent;

                _menu = new ContextMenuStrip();
                _menu.Items.Add("打开配置与诊断", null, (s, e) => OpenConfiguration());
                _menu.Items.Add(new ToolStripSeparator());
                _menu.Items.Add("退出 TIA 集成", null, (s, e) => Shutdown("tray exit"));

                _tray = new NotifyIcon
                {
                    Icon = LoadIcon(),
                    Text = "TAI TIA Portal 集成",
                    ContextMenuStrip = _menu,
                    Visible = true
                };
                _tray.DoubleClick += (s, e) => OpenConfiguration();

                _timer = new System.Windows.Forms.Timer { Interval = TickMs };
                _timer.Tick += OnTick;
                _timer.Start();

                // First scan right away.
                Scan();
            }

            private static Icon LoadIcon()
            {
                try
                {
                    Icon icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                    if (icon != null) return icon;
                }
                catch { }
                return SystemIcons.Application;
            }

            private void OnTick(object sender, EventArgs e)
            {
                if (_shuttingDown) return;
                try
                {
                    if (_stopEvent.WaitOne(0))
                    {
                        Shutdown("stop event");
                        return;
                    }
                    if (++_tick % ScanEveryTicks == 0) Scan();
                }
                catch (Exception ex)
                {
                    Log("Watcher tick failed: " + ex.Message);
                }
            }

            private void Scan()
            {
                if (_scanning || _shuttingDown) return;
                _scanning = true;
                try
                {
                    int[] pids;
                    try
                    {
                        pids = PortalDiscovery.GetPortalProcessIds() ?? new int[0];
                    }
                    catch (Exception ex)
                    {
                        Log("GetPortalProcessIds failed: " + ex.Message);
                        return;
                    }

                    var alive = new HashSet<int>(pids);

                    // Forget TIA processes that are gone. Their helper closes itself on owner exit;
                    // we only drop our handle and never kill it.
                    var gone = new List<int>();
                    foreach (var kv in _helpers)
                        if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
                    foreach (int pid in gone)
                    {
                        Log("TIA process " + pid + " gone; releasing helper tracking.");
                        DisposeChild(_helpers[pid]);
                        _helpers.Remove(pid);
                    }

                    foreach (int pid in pids)
                    {
                        if (_shuttingDown || _stopEvent.WaitOne(0)) return;
                        HelperEntry entry;
                        if (!_helpers.TryGetValue(pid, out entry))
                        {
                            entry = new HelperEntry { OwnerPid = pid };
                            _helpers[pid] = entry;
                        }
                        if (IsChildRunning(entry)) continue;
                        if (entry.Child != null)
                        {
                            int code = -1;
                            try { code = entry.Child.ExitCode; } catch { }
                            Log("Helper for TIA " + pid + " exited (code " + code + ").");
                            DisposeChild(entry);
                        }
                        if (!MayLaunch(entry)) continue;

                        // Only launch for a verified TIA main window (not splash/dialog).
                        IntPtr main;
                        try
                        {
                            if (!PortalDiscovery.IsPortalProcess(pid)) continue;
                            main = PortalDiscovery.FindMainWindow(pid);
                        }
                        catch (Exception ex)
                        {
                            Log("Portal discovery for " + pid + " failed: " + ex.Message);
                            continue;
                        }
                        if (main == IntPtr.Zero) continue;

                        Launch(entry, main);
                    }
                }
                finally
                {
                    _scanning = false;
                }
            }

            private static bool IsChildRunning(HelperEntry entry)
            {
                if (entry.Child == null) return false;
                try { return !entry.Child.HasExited; }
                catch { return false; }
            }

            private static bool MayLaunch(HelperEntry entry)
            {
                DateTime now = DateTime.UtcNow;
                if (now < entry.BackoffUntilUtc) return false;
                if ((now - entry.LastLaunchUtc).TotalSeconds < MinRelaunchSeconds) return false;
                if ((now - entry.BurstStartUtc).TotalSeconds > BurstWindowSeconds)
                {
                    entry.BurstStartUtc = now;
                    entry.BurstCount = 0;
                }
                if (entry.BurstCount >= MaxLaunchesPerWindow)
                {
                    entry.BackoffUntilUtc = now.AddSeconds(BackoffSeconds);
                    entry.BurstStartUtc = entry.BackoffUntilUtc;
                    entry.BurstCount = 0;
                    Log("Helper for TIA " + entry.OwnerPid + " restarted too often; backing off " + BackoffSeconds + " s.");
                    return false;
                }
                return true;
            }

            private void Launch(HelperEntry entry, IntPtr main)
            {
                entry.LastLaunchUtc = DateTime.UtcNow;
                entry.BurstCount++;
                string args = "--embedded --owner " + entry.OwnerPid + " --root " + QuoteArg(_root);
                try
                {
                    var psi = new ProcessStartInfo(Application.ExecutablePath, args)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)
                    };
                    entry.Child = Process.Start(psi);
                    Log("Launched embedded helper pid=" + (entry.Child != null ? entry.Child.Id.ToString() : "?") +
                        " for TIA " + entry.OwnerPid + " main=0x" + main.ToInt64().ToString("X") +
                        " (launch " + entry.BurstCount + "/" + MaxLaunchesPerWindow + ")");
                }
                catch (Exception ex)
                {
                    Log("Launch embedded helper for TIA " + entry.OwnerPid + " failed: " + ex.Message);
                    DisposeChild(entry);
                }
            }

            private void OpenConfiguration()
            {
                try
                {
                    // Default mode = standalone console; its own mutex activates an existing one.
                    var psi = new ProcessStartInfo(Application.ExecutablePath, "--root " + QuoteArg(_root))
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)
                    };
                    using (Process.Start(psi)) { }
                }
                catch (Exception ex)
                {
                    Log("Open configuration failed: " + ex.Message);
                }
            }

            private void Shutdown(string reason)
            {
                if (_shuttingDown) return;
                _shuttingDown = true;
                Log("Integration watcher shutting down (" + reason + ").");
                try { _timer.Stop(); } catch { }

                // Tell every embedded helper (ours and manually launched ones) to stop.
                try { _stopEvent.Set(); } catch (Exception ex) { Log("Set stop event failed: " + ex.Message); }

                // Bounded wait for our own helpers; never kill them.
                var sw = Stopwatch.StartNew();
                foreach (var entry in _helpers.Values)
                {
                    if (entry.Child == null) continue;
                    int remaining = ShutdownWaitMs - (int)sw.ElapsedMilliseconds;
                    try
                    {
                        if (remaining > 0 && !entry.Child.HasExited && !entry.Child.WaitForExit(remaining))
                            Log("Helper pid=" + entry.Child.Id + " for TIA " + entry.OwnerPid + " still running after bounded wait.");
                    }
                    catch { }
                }
                foreach (var entry in _helpers.Values) DisposeChild(entry);
                _helpers.Clear();

                try { _tray.Visible = false; } catch { }
                ExitThread();
            }

            private static void DisposeChild(HelperEntry entry)
            {
                if (entry.Child == null) return;
                try { entry.Child.Dispose(); } catch { }
                entry.Child = null;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try { _timer.Stop(); _timer.Dispose(); } catch { }
                    try { _tray.Visible = false; _tray.Dispose(); } catch { }
                    try { _menu.Dispose(); } catch { }
                    foreach (var entry in _helpers.Values) DisposeChild(entry);
                    _helpers.Clear();
                }
                base.Dispose(disposing);
            }
        }

        // ------------------------------------------------------------------------------------------

        /// <summary>Quotes one argument per CommandLineToArgvW rules (handles trailing backslashes).</summary>
        private static string QuoteArg(string value)
        {
            if (value == null) value = string.Empty;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static readonly object LogGate = new object();

        /// <summary>Private log (same file as the host log) so this file has no dependency on Program internals.</summary>
        private static void Log(string line)
        {
            try
            {
                lock (LogGate)
                {
                    string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TAI", "logs");
                    Directory.CreateDirectory(dir);
                    int pid;
                    using (var me = Process.GetCurrentProcess()) pid = me.Id;
                    File.AppendAllText(Path.Combine(dir, "desktop.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + pid + "] [watcher] " + line + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never break the watcher.
            }
        }
    }
}
