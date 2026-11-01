using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TAI.Desktop
{
    /// <summary>
    /// "--embedded --owner PID": hosts the compact /assistant page as a real WS_CHILD window inside the
    /// verified TIA Portal main window (right side of its client area), plus a draggable floating ball child window that toggles the panel.
    /// The entry works independently of TIA version and toolbar contents.
    ///
    /// Nothing in TIA is patched, injected, hooked, moved or resized: only our own windows are parented
    /// (SetParent) into the TIA main window. If the child cannot be established the helper exits; it never
    /// falls back to a top-level or owned window.
    /// </summary>
    internal static class EmbeddedHost
    {
        public static int Run(HostOptions options)
        {
            int pid = options.OwnerPid;
            if (pid <= 0)
            {
                HostLog.Write("Embedded: --owner PID is required.");
                return 2;
            }

            // Discovery (MSAA/COM) runs on a throwaway thread so no COM/OLE helper window is created on the
            // UI thread before its DPI context matches TIA.
            IntPtr main = IntPtr.Zero;
            bool portal = false;
            RunOnSta(() =>
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    portal = PortalDiscovery.IsPortalProcess(pid);
                    if (!portal) return;
                    main = PortalDiscovery.FindMainWindow(pid);
                    if (main != IntPtr.Zero) return;
                    Thread.Sleep(500);
                }
            });
            if (!portal)
            {
                HostLog.Write("Embedded: process " + pid + " is not a verified TIA Portal process.");
                return 3;
            }
            if (main == IntPtr.Zero)
            {
                HostLog.Write("Embedded: no verified ADWorkbench main window for TIA " + pid + ".");
                return 4;
            }

            bool created;
            using (var mutex = new Mutex(true, @"Local\TAI.Embedded." + pid, out created))
            {
                if (!created)
                {
                    HostLog.Write("Embedded: helper for TIA " + pid + " already running.");
                    return 0;
                }

                // Must happen before ANY HWND is created on this thread (incl. WinForms parking window).
                IntPtr dpiContext = MatchThreadDpi(main);
                if (dpiContext == IntPtr.Zero)
                {
                    HostLog.Write("Embedded: cannot match TIA DPI awareness; refusing to embed.");
                    return 5;
                }

                using (var session = new EmbeddedSession(options, pid, main, dpiContext))
                {
                    if (!session.Start()) return 6;
                    Application.Run(session);
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Sets this thread's DPI awareness context to the one of the TIA main window. Never touches TIA or process DPI.</summary>
        internal static IntPtr MatchThreadDpi(IntPtr main)
        {
            try
            {
                IntPtr target = EmbedNative.GetWindowDpiAwarenessContext(main);
                if (target == IntPtr.Zero)
                {
                    HostLog.Write("Embedded: GetWindowDpiAwarenessContext failed: " + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
                IntPtr before = EmbedNative.GetThreadDpiAwarenessContext();
                EmbedNative.SetThreadDpiAwarenessContext(target);
                IntPtr after = EmbedNative.GetThreadDpiAwarenessContext();
                bool equal = EmbedNative.AreDpiAwarenessContextsEqual(after, target);
                HostLog.Write("Embedded DPI: TIA awareness=" + EmbedNative.GetAwarenessFromDpiAwarenessContext(target) +
                              " thread before=" + EmbedNative.GetAwarenessFromDpiAwarenessContext(before) +
                              " after=" + EmbedNative.GetAwarenessFromDpiAwarenessContext(after) +
                              " matched=" + equal + " TIA dpi=" + EmbedNative.GetDpiForWindow(main));
                return equal ? target : IntPtr.Zero;
            }
            catch (EntryPointNotFoundException ex)
            {
                HostLog.Write("Embedded: per-thread DPI API unavailable (Windows 10 1607+ required): " + ex.Message);
                return IntPtr.Zero;
            }
        }

        internal static void RunOnSta(Action action)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) HostLog.Write("Embedded discovery error: " + error);
        }
    }

    /// <summary>Owns the embedded panel and draggable floating ball. UI thread only.</summary>
    internal sealed class EmbeddedSession : ApplicationContext
    {
        private const int GeometryTickMs = 200;
        private const int DefaultPanelLogicalWidth = 440;
        private const int MinPanelLogicalWidth = 320;
        private const double MaxPanelFraction = 0.6;

        private readonly HostOptions options;
        private readonly int pid;
        private readonly IntPtr main;
        private readonly IntPtr dpiContext;

        private MainForm panel;
        private FloatingBall overlay;
        private Point? ballPosition;
        private readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        private Process owner;
        private EventWaitHandle stopEvent;
        private RegisteredWaitHandle stopWait;

        private int panelLogicalWidth = DefaultPanelLogicalWidth;
        private Rectangle lastPanelRect = Rectangle.Empty;
        private Rectangle lastOverlayRect = Rectangle.Empty;
        private bool shuttingDown;
        private bool panelLost;

        public EmbeddedSession(HostOptions options, int pid, IntPtr main, IntPtr dpiContext)
        {
            this.options = options;
            this.pid = pid;
            this.main = main;
            this.dpiContext = dpiContext;
        }

        public bool Start()
        {
            try
            {
                options.Mode = HostMode.Assistant;
                panel = new MainForm(options, true);
                panel.HideRequested += (s, e) => SetPanelVisible(false);
                panel.WidthDragged += OnPanelWidthDragged;
                panel.HandleDestroyed += (s, e) =>
                {
                    if (shuttingDown || panel.RecreatingHandle) return;
                    // Typically TIA destroyed its main window (our children go with it). Do not dispose
                    // inside WM_DESTROY; the next tick shuts down.
                    HostLog.Write("Embedded panel destroyed externally.");
                    panelLost = true;
                };

                IntPtr ph = panel.Handle; // created parked (WS_CHILD of our message-only parking window)
                if (!Reparent(ph, "panel")) return false;
                // WinForms may recreate a handle (property change); the new one must be re-parented, never top-level.
                panel.HandleCreated += (s, e) =>
                {
                    if (shuttingDown) return;
                    if (!Reparent(panel.Handle, "panel (recreated)")) Shutdown("re-parent failed");
                    else lastPanelRect = Rectangle.Empty;
                };

                overlay = new FloatingBall();
                overlay.Dragged += (s, delta) =>
                {
                    ballPosition = new Point(lastOverlayRect.X + delta.X, lastOverlayRect.Y + delta.Y);
                    LayoutBall(true);
                };
                overlay.Toggle += (s, e) => SetPanelVisible(!panel.Visible);
                IntPtr oh = overlay.Handle;
                overlay.Visible = false;
                if (!Reparent(oh, "floating ball")) return false;
                overlay.HandleCreated += (s, e) =>
                {
                    if (shuttingDown) return;
                    if (!Reparent(overlay.Handle, "floating ball (recreated)")) Shutdown("re-parent failed");
                    else lastOverlayRect = Rectangle.Empty;
                };

                Layout(true);
                panel.Visible = false;  // open from the floating ball
                EmbedNative.SetWindowPos(ph, EmbedNative.HWND_TOP, 0, 0, 0, 0,
                    EmbedNative.SWP_NOMOVE | EmbedNative.SWP_NOSIZE | EmbedNative.SWP_NOACTIVATE);
                panel.StartEmbedded();
                LogProof(ph, "panel");

                try
                {
                    owner = Process.GetProcessById(pid);
                    owner.EnableRaisingEvents = true;
                    owner.Exited += (s, e) => Post(() => Shutdown("TIA process exited"));
                }
                catch (Exception ex)
                {
                    HostLog.Write("Embedded: cannot watch owner process: " + ex.Message);
                    return false;
                }

                try
                {
                    bool createdEvent;
                    stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, IntegrationWatcher.StopEventName, out createdEvent);
                    stopWait = ThreadPool.RegisterWaitForSingleObject(stopEvent,
                        (state, timedOut) => Post(() => Shutdown("stop event")), null, Timeout.Infinite, true);
                }
                catch (Exception ex)
                {
                    HostLog.Write("Embedded: stop event unavailable: " + ex.Message);
                }

                LayoutBall(true);

                tick.Interval = GeometryTickMs;
                tick.Tick += (s, e) => OnTick();
                tick.Start();
                HostLog.Write("Embedded helper running for TIA " + pid + " main=0x" + main.ToInt64().ToString("X"));
                return true;
            }
            catch (Exception ex)
            {
                HostLog.Write("Embedded start failed: " + ex);
                return false;
            }
        }

        /// <summary>SetParent into the TIA main window and verify WS_CHILD / !WS_POPUP / GetParent. No fallback.</summary>
        private bool Reparent(IntPtr h, string what)
        {
            long style = EmbedNative.GetWindowLongPtr(h, EmbedNative.GWL_STYLE).ToInt64();
            long newStyle = (style | EmbedNative.WS_CHILD | EmbedNative.WS_CLIPSIBLINGS | EmbedNative.WS_CLIPCHILDREN) &
                            ~(EmbedNative.WS_POPUP | EmbedNative.WS_CAPTION | EmbedNative.WS_THICKFRAME | EmbedNative.WS_SYSMENU);
            if (newStyle != style) EmbedNative.SetWindowLongPtr(h, EmbedNative.GWL_STYLE, new IntPtr(newStyle));
            long ex = EmbedNative.GetWindowLongPtr(h, EmbedNative.GWL_EXSTYLE).ToInt64();
            long newEx = ex & ~EmbedNative.WS_EX_APPWINDOW;
            if (newEx != ex) EmbedNative.SetWindowLongPtr(h, EmbedNative.GWL_EXSTYLE, new IntPtr(newEx));

            IntPtr previous = EmbedNative.SetParent(h, main);
            int err = Marshal.GetLastWin32Error();
            EmbedNative.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, EmbedNative.SWP_NOMOVE | EmbedNative.SWP_NOSIZE |
                EmbedNative.SWP_NOZORDER | EmbedNative.SWP_NOACTIVATE | EmbedNative.SWP_FRAMECHANGED);
            IntPtr parent = EmbedNative.GetParent(h);
            if (previous == IntPtr.Zero && parent != main)
            {
                HostLog.Write("Embedded: SetParent(" + what + ") failed, error " + err);
                return false;
            }
            return LogProof(h, what);
        }

        private bool LogProof(IntPtr h, string what)
        {
            IntPtr parent = EmbedNative.GetParent(h);
            long style = EmbedNative.GetWindowLongPtr(h, EmbedNative.GWL_STYLE).ToInt64();
            bool child = (style & EmbedNative.WS_CHILD) != 0;
            bool popup = (style & EmbedNative.WS_POPUP) != 0;
            IntPtr root = EmbedNative.GetAncestor(h, EmbedNative.GA_ROOT);
            bool ok = parent == main && child && !popup && root == main;
            HostLog.Write("Embedded proof [" + what + "] hwnd=0x" + h.ToInt64().ToString("X") +
                          " GetParent=0x" + parent.ToInt64().ToString("X") + " main=0x" + main.ToInt64().ToString("X") +
                          " root=0x" + root.ToInt64().ToString("X") + " style=0x" + style.ToString("X") +
                          " WS_CHILD=" + child + " WS_POPUP=" + popup + " ok=" + ok);
            return ok;
        }

        private void Post(Action a)
        {
            try
            {
                if (panel != null && !panel.IsDisposed && panel.IsHandleCreated) panel.BeginInvoke(a);
            }
            catch (InvalidOperationException) { } // includes ObjectDisposedException
        }

        private void SetPanelVisible(bool visible)
        {
            if (shuttingDown || panel == null || panel.IsDisposed) return;
            if (panel.Visible == visible) return;
            if (visible) Layout(true);
            panel.Visible = visible;
            if (visible)
            {
                EmbedNative.SetWindowPos(panel.Handle, EmbedNative.HWND_TOP, 0, 0, 0, 0,
                    EmbedNative.SWP_NOMOVE | EmbedNative.SWP_NOSIZE | EmbedNative.SWP_NOACTIVATE);
                panel.FocusContent();
            }
            if (overlay != null && !overlay.IsDisposed) overlay.PanelOpen = visible;
            LayoutBall(true);
            HostLog.Write("Embedded panel " + (visible ? "shown" : "hidden"));
        }

        private void OnPanelWidthDragged(object sender, int screenDeltaX)
        {
            float scale = Scale();
            int w = (int)Math.Round(panelLogicalWidth * scale) - screenDeltaX;
            panelLogicalWidth = Math.Max(MinPanelLogicalWidth, (int)Math.Round(w / scale));
            Layout(true);
        }

        private float Scale()
        {
            try
            {
                uint dpi = EmbedNative.GetDpiForWindow(main);
                return dpi > 0 ? dpi / 96f : 1f;
            }
            catch (EntryPointNotFoundException) { return 1f; }
        }

        private void OnTick()
        {
            if (shuttingDown) return;
            if (panelLost)
            {
                Shutdown("panel window destroyed");
                return;
            }
            if (!EmbedNative.IsWindow(main) || EmbedNative.GetWindowPid(main) != pid)
            {
                Shutdown("TIA main window gone");
                return;
            }
            if (EmbedNative.IsIconic(main)) return; // children are hidden with the parent; nothing to do

            Layout(false);
            LayoutBall(false);
        }

        private void LayoutBall(bool force)
        {
            if (overlay == null || overlay.IsDisposed) return;
            EmbedNative.RECT c;
            if (!EmbedNative.GetClientRect(main, out c)) return;
            Rectangle rect = FloatingBallGeometry.Clamp(c.Right - c.Left, c.Bottom - c.Top, Scale(), ballPosition);
            if (rect.IsEmpty) { overlay.Visible = false; return; }
            if (!force && rect == lastOverlayRect && overlay.Visible && !IsCoveredBySibling(overlay.Handle)) return;
            EmbedNative.SetWindowPos(overlay.Handle, EmbedNative.HWND_TOP, rect.X, rect.Y, rect.Width, rect.Height,
                EmbedNative.SWP_NOACTIVATE | EmbedNative.SWP_SHOWWINDOW);
            overlay.Visible = true;
            lastOverlayRect = rect;
        }

        /// <summary>Right-side panel inside the TIA client area, below the TIA header. Only our window moves.</summary>
        private void Layout(bool force)
        {
            if (panel == null || panel.IsDisposed) return;
            EmbedNative.RECT c;
            if (!EmbedNative.GetClientRect(main, out c)) return;
            int cw = c.Right - c.Left, ch = c.Bottom - c.Top;
            if (cw <= 0 || ch <= 0) return;

            float scale = Scale();
            int top = Math.Min((int)Math.Round(48 * scale), ch / 3);
            int min = (int)Math.Round(MinPanelLogicalWidth * scale);
            int width = (int)Math.Round(panelLogicalWidth * scale);
            width = Math.Min(Math.Max(width, min), (int)(cw * MaxPanelFraction));
            width = Math.Max(1, Math.Min(width, cw));
            var rect = new Rectangle(cw - width, top, width, Math.Max(1, ch - top));

            IntPtr h = panel.Handle;
            bool needRaise = panel.Visible && IsCoveredBySibling(h);
            if (!force && rect == lastPanelRect && !needRaise) return;
            uint flags = EmbedNative.SWP_NOACTIVATE;
            IntPtr after = EmbedNative.HWND_TOP;
            if (!needRaise && !force) flags |= EmbedNative.SWP_NOZORDER;
            EmbedNative.SetWindowPos(h, after, rect.X, rect.Y, rect.Width, rect.Height, flags);
            if (needRaise && overlay != null && overlay.Visible)
                EmbedNative.SetWindowPos(overlay.Handle, EmbedNative.HWND_TOP, 0, 0, 0, 0,
                    EmbedNative.SWP_NOMOVE | EmbedNative.SWP_NOSIZE | EmbedNative.SWP_NOACTIVATE);
            lastPanelRect = rect;
        }

        /// <summary>True when a sibling (TIA child) above our panel overlaps it.</summary>
        private bool IsCoveredBySibling(IntPtr h)
        {
            EmbedNative.RECT me;
            if (!EmbedNative.GetWindowRect(h, out me)) return false;
            Rectangle mine = Rectangle.FromLTRB(me.Left, me.Top, me.Right, me.Bottom);
            IntPtr overlayHandle = overlay != null && overlay.IsHandleCreated ? overlay.Handle : IntPtr.Zero;
            for (IntPtr s = EmbedNative.GetWindow(h, EmbedNative.GW_HWNDPREV); s != IntPtr.Zero;
                 s = EmbedNative.GetWindow(s, EmbedNative.GW_HWNDPREV))
            {
                if (s == overlayHandle || !EmbedNative.IsWindowVisible(s)) continue;
                EmbedNative.RECT r;
                if (EmbedNative.GetWindowRect(s, out r) && mine.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)))
                    return true;
            }
            return false;
        }

        private void Shutdown(string reason)
        {
            if (shuttingDown) return;
            shuttingDown = true;
            HostLog.Write("Embedded helper closing: " + reason);
            try { tick.Stop(); } catch { }
            try
            {
                if (overlay != null && !overlay.IsDisposed)
                {
                    overlay.Visible = false;
                    overlay.Dispose();
                }
            }
            catch (Exception ex) { HostLog.Write("Overlay dispose: " + ex.Message); }
            try
            {
                if (panel != null && !panel.IsDisposed)
                {
                    panel.Visible = false;
                    panel.Dispose();
                }
            }
            catch (Exception ex) { HostLog.Write("Panel dispose: " + ex.Message); }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                shuttingDown = true;
                try { tick.Dispose(); } catch { }
                try { if (stopWait != null) stopWait.Unregister(null); } catch { }
                try { if (stopEvent != null) stopEvent.Dispose(); } catch { }
                try { if (owner != null) owner.Dispose(); } catch { }
                try { if (overlay != null) overlay.Dispose(); } catch { }
                try { if (panel != null) panel.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal static class EmbedNative
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_CHILD = 0x40000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const uint GA_ROOT = 2;
        public const uint GW_HWNDPREV = 3;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public const uint PW_RENDERFULLCONTENT = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
        [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

        public static int GetWindowPid(IntPtr h)
        {
            uint p;
            GetWindowThreadProcessId(h, out p);
            return (int)p;
        }
    }
}
