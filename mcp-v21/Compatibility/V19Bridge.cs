using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TiaMcpServer.Siemens;

namespace TiaMcpServer.Compatibility
{
    // V19 keeps its monolithic SDK separate from the incompatible V21 split assemblies.
    // Only named public members are used. No arbitrary method/property traversal is exposed.
    public static class V19Program
    {
        public static async Task Main(string[] args)
        {
            var options = CliOptions.ParseArgs(args);
            if ((options.TiaMajorVersion ?? 19) != 19) throw new ArgumentException("This MCP binary supports TIA V19 only.");
            Engineering.TiaMajorVersion = 19;
            AppDomain.CurrentDomain.AssemblyResolve += Engineering.Resolver;
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
            await builder.Build().RunAsync();
        }
    }

    [McpServerToolType]
    public static partial class V19Tools
    {
        private static readonly object Gate = new object();
        private static object? portal;
        private static Assembly? sdk;
        private static Assembly Sdk => sdk ?? (sdk = Engineering.Resolver(null!, new ResolveEventArgs("Siemens.Engineering, Version=19.0.0.0, Culture=neutral, PublicKeyToken=d29ec89bac048f84"))!);
        private static Type Type(string name) => CoreSdkType(name);
        internal static object? Property(object value, string name) => CoreProperty(value, name);
        internal static object? Invoke(object value, string name, params object[] args) => CoreInvoke(value, name, args);
        private static IEnumerable<object> Items(object? value) => value is IEnumerable sequence ? sequence.Cast<object>() : throw new InvalidOperationException("Expected a public SDK collection.");
        private static string Name(object value) => Convert.ToString(Property(value, "Name")) ?? "";
        private static object Project() => CoreSelected().Project;
        private static object? Service(object value, string name)
        {
            var provider = Type("IEngineeringServiceProvider");
            if (!provider.IsInstanceOfType(value)) return null;
            return provider.GetMethods().Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0).MakeGenericMethod(Type(name)).Invoke(value, null);
        }
        private static object Success(string message) => new { Message = message, Meta = new { success = true }, Succeeded = true };

        [McpServerTool(Name = "GetCompatibilityInfo"), Description("Read adapter capabilities, SDK version and documented version-specific limitations without attaching to TIA.")]
        public static object Compatibility() => new { tiaVersion = 19, adapter = "public-api-reflection", sdkLoaded = sdk != null, runtimeVerified = false,
            limitations = new[] { "SIMATIC SD .s7dcl/.s7res document API was introduced after V19; use SIMATIC ML XML or external source generation", "Live V19 validation required on target installation" } };

        [McpServerTool(Name = "Connect"), Description("Attach to an existing TIA V19 instance. Exact processId is required if more than one instance exists. Does not start TIA or open a project.")]
        public static object Connect(int? processId = null)
        {
            lock (Gate)
            {
                if (portal != null) throw new InvalidOperationException("Disconnect the current instance before connecting again.");
                var processes = Items(Invoke(Type("TiaPortal"), "GetProcesses")).ToArray();
                var matches = processes.Where(p => !processId.HasValue || Convert.ToInt32(Property(p, "Id")) == processId.Value).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("Select one exact V19 processId. Available IDs: " + string.Join(", ", processes.Select(p => Property(p, "Id"))));
                portal = Invoke(matches[0], "Attach") ?? throw new InvalidOperationException("TIA Attach returned no portal.");
                return Success("Connected to TIA V19");
            }
        }
        [McpServerTool(Name = "Disconnect"), Description("Release this bridge's attachment without closing TIA.")]
        public static object Disconnect() { lock (Gate) { (portal as IDisposable)?.Dispose(); portal = null; return Success("Disconnected"); } }
        [McpServerTool(Name = "GetState"), Description("Read connection, project and local-session state. A connected portal without an open project is valid.")]
        public static object State() { lock (Gate) return CoreState(); }
        [McpServerTool(Name = "GetProject"), Description("Read currently open V19 projects and local sessions with advertised SDK attributes.")]
        public static object GetProject() { lock (Gate) return CoreProjects(); }

        internal sealed class Entry
        {
            public string kind { get; set; } = "";
            public string path { get; set; } = "";
            public string name { get; set; } = "";
            public string type { get; set; } = "";
            public bool ambiguous { get; set; }
            public string devicePath { get; set; } = "";
            public string? deviceItemPath { get; set; }
            public string? softwarePath { get; set; }
            [JsonIgnore] public object Target { get; set; } = null!;
        }
        private static Entry Row(object value, string kind, string path) => new Entry { Target = value, kind = kind, path = path, name = Name(value), type = value.GetType().Name };
        private static List<Entry> Inventory()
        {
            var project = Project(); var rows = new List<Entry>(); var seen = new HashSet<object>();
            void AddItem(object item, string parent, string devicePath, int depth)
            {
                if (depth > 64) throw new InvalidOperationException("Device hierarchy exceeds 64 levels.");
                var p = parent + "/" + Name(item); var itemRow = Row(item, "deviceItem", p);
                itemRow.devicePath = devicePath; itemRow.deviceItemPath = p; rows.Add(itemRow);
                var container = Service(item, "HW.Features.SoftwareContainer");
                var software = container == null ? null : Property(container, "Software");
                if (software != null)
                {
                    var softwareRow = Row(software, "software", p); softwareRow.devicePath = devicePath;
                    softwareRow.deviceItemPath = p; softwareRow.softwarePath = p; rows.Add(softwareRow);
                }
                foreach (var child in Items(Property(item, "DeviceItems"))) AddItem(child, p, devicePath, depth + 1);
            }
            void AddDevices(object collection, string prefix)
            {
                foreach (var device in Items(collection))
                {
                    if (!seen.Add(device)) continue;
                    var p = prefix + Name(device); var deviceRow = Row(device, "device", p); deviceRow.devicePath = p; rows.Add(deviceRow);
                    foreach (var item in Items(Property(device, "DeviceItems"))) AddItem(item, p, p, 0);
                }
            }
            void AddGroups(object collection, string prefix, int depth)
            {
                if (depth > 64) throw new InvalidOperationException("Group hierarchy exceeds 64 levels.");
                foreach (var group in Items(collection)) { var p = prefix + Name(group) + "/"; AddDevices(Property(group, "Devices")!, p); AddGroups(Property(group, "Groups")!, p, depth + 1); }
            }
            AddDevices(Property(project, "Devices")!, ""); AddGroups(Property(project, "DeviceGroups")!, "", 0);
            var ungrouped = Property(project, "UngroupedDevicesGroup"); if (ungrouped != null) AddDevices(Property(ungrouped, "Devices")!, "");
            foreach (var duplicate in rows.GroupBy(r => new { r.kind, r.path }).Where(g => g.Count() > 1)) foreach (var row in duplicate) row.ambiguous = true;
            return rows;
        }
        private static object Find(string kind, string path)
        {
            string projectName = Name(Project());
            var matches = Inventory().Where(e => e.kind == kind && (e.path == path || "Devices/" + e.path == path ||
                projectName + "/" + e.path == path || projectName + "/Devices/" + e.path == path)).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Copy one unique exact " + kind + " path from GetProjectTree.Paths.");
            return matches[0].Target;
        }
        [McpServerTool(Name = "GetProjectTree"), Description("Read V19 devices, nested groups, device items and software canonical paths. Copy exact paths and kind into subsequent tool calls.")]
        public static object Tree() { lock (Gate) { var rows = Inventory(); return new { Paths = rows, Tree = string.Join("\n", rows.Select(r => r.kind + ": " + r.path)), PathContract = "Copy exact case-sensitive paths by kind; software uses its owning device-item path. Project/Devices prefixes are accepted only when unique. Ambiguous paths are rejected." }; } }
        [McpServerTool(Name = "GetDevices"), Description("Read the V19 device and software path inventory.")]
        public static object Devices() { lock (Gate) { var rows = Inventory(); return new { Items = rows.Where(r => r.kind == "device").Select(r => CoreInfo(r.Target, r.path)).ToArray(), Paths = rows, PathContract = "Copy exact case-sensitive paths by kind. Software uses its owning device-item path; ambiguous paths are rejected.", Meta = new { success = true } }; } }
        private static object Info(object target) => CoreInfo(target, "");
        [McpServerTool(Name = "GetSoftwareInfo"), Description("Read a software name and type from its exact inventory path.")]
        public static object SoftwareInfo(string softwarePath) { lock (Gate) return Info(Find("software", softwarePath)); }
        [McpServerTool(Name = "GetDeviceInfo"), Description("Read a device name and type from its exact inventory path.")]
        public static object DeviceInfo(string devicePath) { lock (Gate) return Info(Find("device", devicePath)); }
        private static List<Entry> Blocks(string softwarePath)
        {
            var rows = new List<Entry>(); var software = Find("software", softwarePath);
            void Walk(object group, string prefix, int depth)
            {
                if (depth > 64) throw new InvalidOperationException("Block group hierarchy exceeds 64 levels.");
                foreach (var block in Items(Property(group, "Blocks"))) rows.Add(Row(block, "block", prefix + Name(block)));
                foreach (var child in Items(Property(group, "Groups"))) Walk(child, prefix + Name(child) + "/", depth + 1);
            }
            Walk(Property(software, "BlockGroup")!, "", 0); return rows;
        }
        private static object Block(string softwarePath, string blockPath)
        {
            var root = Name(Property(Find("software", softwarePath), "BlockGroup")!);
            var matches = Blocks(softwarePath).Where(e => e.path == blockPath || root + "/" + e.path == blockPath).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Copy a unique exact blockPath from GetBlocks.Items.path.");
            return matches[0].Target;
        }
        [McpServerTool(Name = "GetBlocks"), Description("Read V19 PLC user blocks, metadata and exact paths, filtered by optional regular expression. Use BackupSoftwareForEdit for a full block backup.")]
        public static object GetBlocks(string softwarePath, string regexName = "") { lock (Gate) return CoreGetBlocks(softwarePath, regexName); }
        [McpServerTool(Name = "GetBlockInfo"), Description("Read a V19 user block name, programming language and consistency.")]
        public static object BlockInfo(string softwarePath, string blockPath)
        {
            lock (Gate) return CoreBlockInfo(Block(softwarePath, blockPath), blockPath);
        }
        [McpServerTool(Name = "ExportBlock"), Description("Export one V19 block as SIMATIC ML XML into a directory, optionally preserving groups. An explicit new .xml file is also accepted. Existing files are never overwritten.")]
        public static object ExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath = false)
        {
            lock (Gate) return CoreExportBlock(softwarePath, blockPath, exportPath, preservePath);
        }
        [McpServerTool(Name = "CompileSoftware"), Description("Compile the selected V19 software via the public ICompilable service. Returns actual SDK error and warning counts.")]
        public static object Compile(string softwarePath, string password = "")
        {
            lock (Gate)
            {
                CoreSafetyLogin(softwarePath, password);
                var compiler = Service(Find("software", softwarePath), "Compiler.ICompilable") ?? throw new NotSupportedException("This software has no public compiler service.");
                var result = Invoke(compiler, "Compile")!;
                var errors = Convert.ToInt32(Property(result, "ErrorCount")); var warnings = Convert.ToInt32(Property(result, "WarningCount"));
                var state = Convert.ToString(Property(result, "State"));
                var succeeded = errors == 0 && new[] { "Success", "Information", "Warning" }.Contains(state);
                return new { Succeeded = succeeded, State = state, ErrorCount = errors, WarningCount = warnings, Messages = CompileMessages(Property(result, "Messages")!, 0), Meta = new { success = succeeded } };
            }
        }
        private static object[] CompileMessages(object messages, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("Compiler message tree exceeds 64 levels.");
            return Items(messages).Select(m => (object)new { Description = Convert.ToString(Property(m, "Description")), State = Convert.ToString(Property(m, "State")), Path = Convert.ToString(CoreOptional(m, "Path")), ErrorCount = CoreOptional(m, "ErrorCount"), WarningCount = CoreOptional(m, "WarningCount"), Messages = CompileMessages(Property(m, "Messages")!, depth + 1) }).ToArray();
        }
        [McpServerTool(Name = "SaveProject"), Description("Save the currently attached V19 local project. Caller must verify compilation of any changed software first.")]
        public static object Save() { lock (Gate) { var selected = CoreSelected(); Invoke(selected.Session ?? selected.Project, "Save"); return Success("V19 project/session saved"); } }
    }
}
