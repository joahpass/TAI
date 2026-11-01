using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace TAI.Desktop
{
    /// <summary>
    /// Discovery of running TIA Portal processes and their native main window (MSAA name "ADWorkbench").
    /// Read-only: no input, no accessibility actions, no window changes.
    /// </summary>
    internal static class PortalDiscovery
    {
        internal const string PortalExeName = "Siemens.Automation.Portal.exe";
        internal const string PortalProcessName = "Siemens.Automation.Portal";
        internal const string MainWindowAccName = "ADWorkbench";

        public static bool IsPortalProcess(int pid)
        {
            if (pid <= 0) return false;
            string path = CopilotNative.GetProcessImagePath(pid);
            if (string.IsNullOrEmpty(path)) return false;
            string file;
            try { file = Path.GetFileName(path); }
            catch (ArgumentException) { return false; }
            return string.Equals(file, PortalExeName, StringComparison.OrdinalIgnoreCase);
        }

        public static int[] GetPortalProcessIds()
        {
            var result = new List<int>();
            Process[] procs;
            try { procs = Process.GetProcessesByName(PortalProcessName); }
            catch (Exception) { return new int[0]; }
            foreach (Process p in procs)
            {
                try
                {
                    int id = p.Id;
                    if (IsPortalProcess(id)) result.Add(id);
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }
            result.Sort();
            return result.ToArray();
        }

        /// <summary>
        /// Returns the unique visible top-level window of <paramref name="pid"/> whose MSAA client name is
        /// exactly "ADWorkbench". Splash screens/dialogs do not carry that name. Zero when missing or ambiguous.
        /// </summary>
        public static IntPtr FindMainWindow(int pid)
        {
            if (!IsPortalProcess(pid)) return IntPtr.Zero;
            var candidates = new List<IntPtr>();
            CopilotNative.EnumWindows((hwnd, lp) =>
            {
                if (CopilotNative.GetWindowPid(hwnd) == pid && CopilotNative.IsWindowVisible(hwnd))
                    candidates.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            IntPtr found = IntPtr.Zero;
            int count = 0;
            foreach (IntPtr hwnd in candidates)
            {
                string name, desc; int role;
                if (!CopilotMsaa.TryRead(hwnd, out name, out role, out desc)) continue;
                if (string.Equals(name, MainWindowAccName, StringComparison.Ordinal))
                {
                    found = hwnd;
                    count++;
                }
            }
            return count == 1 ? found : IntPtr.Zero;
        }

        /// <summary>True when hwnd is a live, visible, non-minimized ADWorkbench main of pid.</summary>
        internal static bool IsValidMain(int pid, IntPtr main)
        {
            if (main == IntPtr.Zero || !CopilotNative.IsWindow(main)) return false;
            if (CopilotNative.GetWindowPid(main) != pid) return false;
            if (!CopilotNative.IsWindowVisible(main) || CopilotNative.IsIconic(main)) return false;
            if (CopilotNative.GetAncestor(main, CopilotNative.GA_ROOT) != main) return false;
            string name, desc; int role;
            if (!CopilotMsaa.TryRead(main, out name, out role, out desc)) return false;
            return string.Equals(name, MainWindowAccName, StringComparison.Ordinal);
        }
    }

    /// <summary>Validated original Copilot (robot) button. Bounds are screen coordinates in the caller thread's DPI context, clipped by ancestors.</summary>
    internal sealed class CopilotTarget
    {
        public CopilotTarget(IntPtr mainWindow, IntPtr buttonWindow, IntPtr commandWindow, Rectangle bounds)
        {
            MainWindow = mainWindow;
            ButtonWindow = buttonWindow;
            CommandWindow = commandWindow;
            Bounds = bounds;
        }

        public IntPtr MainWindow { get; private set; }
        public IntPtr ButtonWindow { get; private set; }
        /// <summary>The Application.GoToCopilot_ PEUserControl container (direct parent of ButtonWindow).</summary>
        public IntPtr CommandWindow { get; private set; }
        public Rectangle Bounds { get; private set; }

        public override string ToString()
        {
            return string.Format("CopilotTarget main=0x{0:X} button=0x{1:X} command=0x{2:X} bounds={3}",
                MainWindow.ToInt64(), ButtonWindow.ToInt64(), CommandWindow.ToInt64(), Bounds);
        }
    }

    /// <summary>
    /// Semantic, coordinate-free locator for TIA's original Copilot icon using direct MSAA (AccessibleObjectFromWindow).
    /// Chain: PEIconicButton(role 43) -> "Application.GoToCopilot_" PEUserControl -> "MainToolbar" PEToolBar(role 22)
    ///        -> "ApplicationHeader" PEApplicationHeader -> "ADWorkbench" main.
    /// Fails closed (null) on missing, ambiguous, mismatched, or hidden chains. Callers must throttle full scans.
    /// Uses late-bound IDispatch for IAccessible, so no Accessibility assembly reference is required.
    /// </summary>
    internal static class CopilotLocator
    {
        internal const string CommandName = "Application.GoToCopilot_";
        internal const string CommandDesc = "PEUserControl";
        internal const string ToolbarName = "MainToolbar";
        internal const string ToolbarDescPrefix = "PEToolBar";
        internal const string HeaderName = "ApplicationHeader";
        internal const string HeaderDescPrefix = "PEApplicationHeader";
        internal const string ButtonDesc = "PEIconicButton";
        internal const int RoleClient = 10;
        internal const int RoleToolbar = 22;
        internal const int RolePushButton = 43;

        public static CopilotTarget Find(int pid, IntPtr main)
        {
            try
            {
                return FindCore(pid, main);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static CopilotTarget FindCore(int pid, IntPtr main)
        {
            if (!PortalDiscovery.IsValidMain(pid, main)) return null;

            // Enumerate all native descendants of main; keep visible ones of the same process.
            var descendants = new List<IntPtr>();
            CopilotNative.EnumChildWindows(main, (hwnd, lp) =>
            {
                descendants.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            IntPtr command = IntPtr.Zero;
            int commandCount = 0;
            foreach (IntPtr hwnd in descendants)
            {
                if (CopilotNative.GetWindowPid(hwnd) != pid || !CopilotNative.IsWindowVisible(hwnd)) continue;
                string name, desc; int role;
                if (!CopilotMsaa.TryRead(hwnd, out name, out role, out desc)) continue;
                if (!string.Equals(name, CommandName, StringComparison.Ordinal)) continue;
                commandCount++;
                command = hwnd;
                if (commandCount > 1) return null; // ambiguous
            }
            if (commandCount != 1) return null;

            // Validate the command container itself.
            {
                string name, desc; int role;
                if (!CopilotMsaa.TryRead(command, out name, out role, out desc)) return null;
                if (role != RoleClient || !string.Equals(desc, CommandDesc, StringComparison.Ordinal)) return null;
            }

            // Validate ancestry: command -> toolbar -> header -> main.
            IntPtr toolbar = CopilotNative.GetParentWindow(command);
            if (!CheckNode(toolbar, pid, ToolbarName, ToolbarDescPrefix, RoleToolbar)) return null;
            IntPtr header = CopilotNative.GetParentWindow(toolbar);
            if (!CheckNode(header, pid, HeaderName, HeaderDescPrefix, -1)) return null;
            if (CopilotNative.GetParentWindow(header) != main) return null;

            // Exactly one suitable visible direct child: role 43 / PEIconicButton.
            IntPtr button = IntPtr.Zero;
            int buttonCount = 0;
            for (IntPtr child = CopilotNative.GetWindow(command, CopilotNative.GW_CHILD);
                 child != IntPtr.Zero;
                 child = CopilotNative.GetWindow(child, CopilotNative.GW_HWNDNEXT))
            {
                if (CopilotNative.GetWindowPid(child) != pid || !CopilotNative.IsWindowVisible(child)) continue;
                string name, desc; int role;
                if (!CopilotMsaa.TryRead(child, out name, out role, out desc)) continue;
                if (role != RolePushButton || !string.Equals(desc, ButtonDesc, StringComparison.Ordinal)) continue;
                buttonCount++;
                button = child;
                if (buttonCount > 1) return null;
            }
            if (buttonCount != 1) return null;
            if (CopilotNative.GetParentWindow(button) != command) return null;
            if (CopilotNative.GetAncestor(button, CopilotNative.GA_ROOT) != main) return null;

            // CopilotNative geometry (never accLocation), clipped against every ancestor's client area up to main.
            Rectangle bounds;
            if (!CopilotNative.TryGetWindowRect(button, out bounds)) return null;
            IntPtr ancestor = command;
            while (ancestor != IntPtr.Zero)
            {
                Rectangle client;
                if (!CopilotNative.TryGetClientScreenRect(ancestor, out client)) return null;
                bounds = Rectangle.Intersect(bounds, client);
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                if (ancestor == main) break;
                ancestor = CopilotNative.GetParentWindow(ancestor);
            }
            if (ancestor != main) return null;

            // Final revalidation: still visible / same process / same main.
            if (!CopilotNative.IsWindowVisible(button) || CopilotNative.GetWindowPid(button) != pid) return null;
            if (!CopilotNative.IsWindowVisible(main) || CopilotNative.IsIconic(main)) return null;

            return new CopilotTarget(main, button, command, bounds);
        }

        private static bool CheckNode(IntPtr hwnd, int pid, string expectedName, string descPrefix, int expectedRole)
        {
            if (hwnd == IntPtr.Zero || !CopilotNative.IsWindow(hwnd)) return false;
            if (CopilotNative.GetWindowPid(hwnd) != pid || !CopilotNative.IsWindowVisible(hwnd)) return false;
            string name, desc; int role;
            if (!CopilotMsaa.TryRead(hwnd, out name, out role, out desc)) return false;
            if (!string.Equals(name, expectedName, StringComparison.Ordinal)) return false;
            if (desc == null || !desc.StartsWith(descPrefix, StringComparison.Ordinal)) return false;
            if (expectedRole >= 0 && role != expectedRole) return false;
            return true;
        }
    }

    /// <summary>Direct MSAA reads (child id 0 / CHILDID_SELF) through late-bound IDispatch; always releases COM.</summary>
    internal static class CopilotMsaa
    {
        private const uint OBJID_CLIENT = 0xFFFFFFFC;
        private static Guid IID_IAccessible = new Guid("618736e0-3c3d-11cf-810c-00aa00389b71");

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwObjectID, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppvObject);

        internal static bool TryRead(IntPtr hwnd, out string name, out int role, out string description)
        {
            name = null; description = null; role = -1;
            if (hwnd == IntPtr.Zero) return false;
            object acc = null;
            try
            {
                int hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref IID_IAccessible, out acc);
                if (hr < 0 || acc == null) return false;
                name = GetString(acc, "accName");
                description = GetString(acc, "accDescription");
                object r = GetProp(acc, "accRole");
                if (r is int) role = (int)r;
                else if (r != null)
                {
                    try { role = Convert.ToInt32(r); } catch (Exception) { role = -1; }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (acc != null && Marshal.IsComObject(acc))
                {
                    try { Marshal.ReleaseComObject(acc); } catch (Exception) { }
                }
            }
        }

        private static string GetString(object acc, string prop)
        {
            object v = GetProp(acc, prop);
            return v as string;
        }

        private static object GetProp(object acc, string prop)
        {
            try
            {
                object v = acc.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, acc, new object[] { 0 });
                if (v != null && Marshal.IsComObject(v))
                {
                    try { Marshal.ReleaseComObject(v); } catch (Exception) { }
                    return null;
                }
                return v;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    internal static class CopilotNative
    {
        internal const uint GA_PARENT = 1;
        internal const uint GA_ROOT = 2;
        internal const uint GW_HWNDNEXT = 2;
        internal const uint GW_CHILD = 5;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        internal delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder exeName, ref uint size);

        internal static int GetWindowPid(IntPtr hwnd)
        {
            uint pid;
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out pid) == 0) return 0;
            return unchecked((int)pid);
        }

        /// <summary>CopilotNative parent (not owner).</summary>
        internal static IntPtr GetParentWindow(IntPtr hwnd)
        {
            return hwnd == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hwnd, GA_PARENT);
        }

        internal static bool TryGetWindowRect(IntPtr hwnd, out Rectangle rect)
        {
            RECT r;
            if (!GetWindowRect(hwnd, out r)) { rect = Rectangle.Empty; return false; }
            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return rect.Width > 0 && rect.Height > 0;
        }

        internal static bool TryGetClientScreenRect(IntPtr hwnd, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            RECT r;
            if (!GetClientRect(hwnd, out r)) return false;
            var tl = new POINT { X = r.Left, Y = r.Top };
            var br = new POINT { X = r.Right, Y = r.Bottom };
            if (!ClientToScreen(hwnd, ref tl) || !ClientToScreen(hwnd, ref br)) return false;
            rect = Rectangle.FromLTRB(Math.Min(tl.X, br.X), tl.Y, Math.Max(tl.X, br.X), br.Y);
            return rect.Width > 0 && rect.Height > 0;
        }

        internal static string GetProcessImagePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                return sb.ToString(0, (int)size);
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }
}
