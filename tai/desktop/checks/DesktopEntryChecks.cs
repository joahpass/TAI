#if DESKTOP_ENTRY_TESTS
using System;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace TAI.Desktop
{
    internal static class DesktopEntryChecks
    {
        private static int checks;

        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAILED: " + name);
            checks++;
        }

        [STAThread]
        private static int Main()
        {
            // Resolve existing dependencies without copying or installing them.
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string name = new AssemblyName(args.Name).Name + ".dll";
                string path = Path.Combine(Environment.CurrentDirectory, "bin", "Release", name);
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            return Run();
        }

        private static int Run()
        {
            string origin = ServerLauncher.BaseUrl;
            Check(HostModes.StartUrl(HostMode.Assistant) == origin + "workspace", "standalone workspace");
            Check(HostModes.StartUrl(HostMode.Assistant, true) == origin + "assistant", "embedded chat");
            Check(HostModes.StartUrl(HostMode.Console) == origin, "console root");
            Check(HostOptions.Parse(new[] { "--assistant" }).Mode == HostMode.Assistant, "assistant option");
            Check(HostOptions.Parse(new[] { "--embedded", "--owner", "123" }).Embedded, "embedded option");
            Check(HostOptions.Parse(new[] { "--embedded", "--owner", "123" }).OwnerPid == 123, "owner retained");

            string design = origin + "workspace?design=fixture%2F42#current";
            Check(MainForm.IsLocalPage(design, "/workspace"), "workspace query and fragment accepted");
            Check(MainForm.IsLocalPage(origin + "settings?tab=model", "/settings"), "settings configuration link");
            Check(MainForm.IsLocalConsoleRoot(origin + "?tab=model#current"), "legacy root link");
            Check(!MainForm.IsLocalConsoleRoot(design), "workspace distinct from console");
            Check(!MainForm.IsLocalPage(origin + "workspace-other?design=42", "/workspace"), "exact route match");
            Check(!MainForm.IsLocalPage("https://example.com/workspace?design=42", "/workspace"), "external origin excluded");
            Check(!MainForm.IsLocalPage("http://127.0.0.1:1/workspace?design=42", "/workspace"), "wrong local port excluded");
            Check(!MainForm.IsLocalPage("about:blank", "/workspace"), "blank popup excluded");
            Check(!MainForm.IsLocalPage("/workspace?design=42", "/workspace"), "relative URI excluded");
            Check(MainForm.IsAllowedUri(origin + "assistant"), "embedded navigation allowed");

            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var boundsMethod = typeof(MainForm).GetMethod("ConsoleBounds", flags);
            var area = new Rectangle(0, 0, 1920, 1080);
            var bounds = (Rectangle)boundsMethod.Invoke(null, new object[] { area, 1f });
            Check(bounds.Size == new Size(1200, 820), "workspace initial logical size");
            Check(bounds.X == 360 && bounds.Y == 130, "workspace centered");
            var smallArea = new Rectangle(-800, 0, 800, 600);
            bounds = (Rectangle)boundsMethod.Invoke(null, new object[] { smallArea, 1.5f });
            Check(smallArea.Contains(bounds), "scaled workspace clamped to monitor");
            Check((Size)typeof(MainForm).GetField("ConsoleMinimum", flags).GetValue(null) == new Size(720, 520), "workspace minimum");
            Check((Size)typeof(MainForm).GetField("AssistantSize", flags).GetValue(null) == new Size(520, 860), "embedded initial size retained");
            Check((Size)typeof(MainForm).GetField("AssistantMinimum", flags).GetValue(null) == new Size(380, 480), "compact constants retained");
            Console.WriteLine("PASS: " + checks + " desktop entry checks; no windows, server or TIA started.");
            return 0;
        }
    }
}
#endif
