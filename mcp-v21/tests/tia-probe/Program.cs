using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TiaProbe
{
    // Read-only diagnostic: enumerate Openness-visible TIA processes and optionally attach.
    // Creates/modifies/saves nothing in any project. Mirrors the MCP server's load strategy.
    internal static class Program
    {
        private static string api;

        private static int Main(string[] args)
        {
            var location = Environment.GetEnvironmentVariable("TiaPortalLocation") ?? @"E:\TIA Portal\Portal V21";
            api = Path.Combine(location, @"PublicAPI\V21\net48");
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            Console.WriteLine("user=" + Environment.UserDomainName + "\\" + Environment.UserName);
            Console.WriteLine("elevated=" + IsElevated());
            Console.WriteLine("api=" + api);
            if (args.Length > 0 && args[0] == "--mta")
            {
                var rest = args.Skip(1).ToArray();
                var rc = 99;
                var t = new System.Threading.Thread(() => { rc = Probe(rest); });
                t.SetApartmentState(System.Threading.ApartmentState.MTA);
                t.Start(); t.Join();
                Console.WriteLine("apartment=MTA");
                return rc;
            }
            return Probe(args);
        }

        private static Assembly Resolve(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            if (!name.StartsWith("Siemens.Engineering", StringComparison.Ordinal)) return null;
            var file = Path.Combine(api, name + ".dll");
            if (!File.Exists(file) && Environment.GetEnvironmentVariable("TAI_PROBE_PORTALAPI") == "1")
            {
                var portal = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(api)), "PublicAPI", name + ".dll");
                if (File.Exists(portal)) file = portal;
            }
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Probe(string[] args)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("TAI_PROBE_TOUCH") == "1")
                {
                    // Mirror the MCP server, which loads the Step7/WinCC tool types before connecting.
                    Console.WriteLine("touch Step7=" + typeof(Siemens.Engineering.SW.PlcSoftware).FullName);
                    Console.WriteLine("touch Hmi=" + typeof(Siemens.Engineering.Hmi.HmiTarget).FullName);
                }
                if (Environment.GetEnvironmentVariable("TAI_PROBE_API") == "1")
                {
                    // Mirror TiaMcpServer.Program: it calls Api.Global.Openness().IsUserInGroup() before any tool runs.
                    var asm = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Siemens.Collaboration.Net.dll"));
                    var api = asm.GetType("Siemens.Collaboration.Net.Api", true);
                    var global = api.GetProperty("Global").GetValue(null);
                    var openness = global.GetType().GetMethod("Openness").Invoke(global, null);
                    var inGroup = openness.GetType().GetMethod("IsUserInGroup").Invoke(openness, null);
                    Console.WriteLine("Api.Global.Openness().IsUserInGroup()=" + inGroup);
                }
                var preload = Environment.GetEnvironmentVariable("TAI_PROBE_PRELOAD");
                if (!string.IsNullOrEmpty(preload))
                {
                    var loaded = Assembly.LoadFrom(preload);
                    Console.WriteLine("preloaded " + loaded.GetName().Name + " " + loaded.GetName().Version);
                }
                var processes = Siemens.Engineering.TiaPortal.GetProcesses();
                Console.WriteLine("GetProcesses count=" + processes.Count);
                foreach (var p in processes) Console.WriteLine("  id=" + p.Id);
                if (args.Length > 0 && int.TryParse(args[0], out var wanted))
                {
                    var target = processes.FirstOrDefault(p => p.Id == wanted);
                    if (target == null) { Console.WriteLine("requested pid not visible"); return 3; }
                    using (var portal = target.Attach())
                    {
                        Console.WriteLine("attach ok");
                        Console.WriteLine("projects=" + portal.Projects.Count + " sessions=" + portal.LocalSessions.Count);
                        foreach (var project in portal.Projects) Console.WriteLine("  project=" + project.Name);
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                for (var e = ex; e != null; e = e.InnerException)
                    Console.WriteLine("ERROR " + e.GetType().FullName + ": " + e.Message);
                return 1;
            }
        }

        private static bool IsElevated()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
