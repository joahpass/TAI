using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Compatibility
{
    public sealed class V19HmiReadRecord
    {
        public string path { get; set; } = "";
        public string parentPath { get; set; } = "";
        public string name { get; set; } = "";
        public string kind { get; set; } = "";
        public string type { get; set; } = "";
        public string contentExport { get; set; } = "Not applicable to this record.";
        public List<object> attributes { get; set; } = new List<object>();
        public List<object> properties { get; set; } = new List<object>();
        public List<string> diagnostics { get; set; } = new List<string>();
    }
    public sealed class V19HmiReadResult
    {
        public bool Succeeded { get; set; }
        public JsonObject Meta { get; set; } = new JsonObject { ["success"] = false };
        public string Message { get; set; } = "";
        public string softwarePath { get; set; } = "";
        public string requestedPath { get; set; } = "";
        public string resolvedSoftwarePath { get; set; } = "";
        public string softwareType { get; set; } = "";
        public Dictionary<string, string> capabilities { get; set; } = new Dictionary<string, string>();
        public List<V19HmiReadRecord> records { get; set; } = new List<V19HmiReadRecord>();
        public List<string> diagnostics { get; set; } = new List<string>();
        public int offset { get; set; }
        public int limit { get; set; }
        public bool hasMore { get; set; }
        public int? nextOffset { get; set; }
        public bool complete { get; set; }
    }
    public static partial class V19Tools
    {
        private sealed class HmiRow
        {
            public object Object = null!;
            public object? Composition;
            public string Path = "", Parent = "", Name = "", Kind = "";
        }
        private sealed class HmiInfo
        {
            public string Name = "", AccessMode = "";
            public System.Type[] SupportedTypes = Array.Empty<System.Type>();
            public bool Read, Write;
        }
        // SDK engineering members may be explicit interface implementations.
        private static object? HmiEngineering(object value, string method, params object[] args)
        {
            var contract = Type("IEngineeringObject");
            if (!contract.IsInstanceOfType(value)) throw new NotSupportedException("Object does not implement public IEngineeringObject.");
            var methods = new[] { contract }.Concat(contract.GetInterfaces()).SelectMany(t => t.GetMethods())
                .Where(m => m.Name == method && !m.IsGenericMethod && m.GetParameters().Length == args.Length)
                .Where(m => m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x))
                .Distinct().ToArray();
            if (methods.Length != 1) throw new MissingMethodException(contract.FullName, method);
            try { return methods[0].Invoke(value, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static List<HmiInfo> HmiInfos(object value) => Items(HmiEngineering(value, "GetAttributeInfos")).Select(i => {
            var access = Property(i, "AccessMode")!;
            var names = access.ToString()!.Split(',').Select(s => s.Trim()).ToArray();
            return new HmiInfo { Name = (string)Property(i, "Name")!, AccessMode = access.ToString()!,
                Read = names.Contains("Read") || names.Contains("ReadWrite"), Write = names.Contains("Write") || names.Contains("ReadWrite"),
                SupportedTypes = Items(Property(i, "SupportedTypes")).Cast<System.Type>().ToArray() };
        }).ToList();
        private static object? HmiAttribute(object value, string name) => HmiEngineering(value, "GetAttribute", name);
        private static bool HmiIs(object value, string fullName)
        {
            for (var t = value.GetType(); t != null; t = t.BaseType) if (t.FullName == fullName) return true;
            return false;
        }
        private static bool HmiClassic(object value) => HmiIs(value, "Siemens.Engineering.Hmi.HmiTarget");
        private static bool HmiUnified(object value) => HmiIs(value, "Siemens.Engineering.HmiUnified.HmiSoftware");
        private static void HmiValidatePath(string path, bool allowEmpty = false)
        {
            if (path == null || (!allowEmpty && path.Length == 0) || path.Any(char.IsControl) ||
                (path.Length != 0 && path.Split('/').Any(p => p.Length == 0 || p == "." || p == "..")))
                throw new ArgumentException("Use exact slash-separated canonical paths without empty/traversal segments or control characters.");
        }
        private static string HmiJoin(string parent, string name)
        {
            HmiValidatePath(name);
            if (name.Contains("/")) throw new InvalidOperationException("HMI name contains a path separator.");
            return parent.Length == 0 ? name : parent + "/" + name;
        }
        private static object HmiSoftware(string path)
        {
            HmiValidatePath(path);
            var value = Find("software", path);
            if (!HmiClassic(value) && !HmiUnified(value)) throw new NotSupportedException("Expected Classic HmiTarget or Unified HmiSoftware.");
            return value;
        }
        private static HmiRow HmiEntry(object value, string parent, string name, string kind, object? composition = null)
            => new HmiRow { Object = value, Parent = parent, Name = name, Path = HmiJoin(parent, name), Kind = kind, Composition = composition };
        private static bool HmiHas(object value, string property) => value.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance) != null;
        private static IEnumerable<object> HmiCollection(object value, string property) => Items(Property(value, property));
        private static IEnumerable<HmiRow> HmiFolder(object folder, string parent, string collection, string kind, bool unified, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("HMI hierarchy exceeds depth 64.");
            var root = HmiEntry(folder, parent, Name(folder), "folder"); yield return root;
            foreach (var row in HmiObjects(folder, root.Path, collection, kind, unified)) yield return row;
            foreach (var child in HmiCollection(folder, unified ? "Groups" : "Folders"))
                foreach (var row in HmiFolder(child, root.Path, collection, kind, unified, depth + 1)) yield return row;
        }
        private static IEnumerable<HmiRow> HmiObjects(object owner, string parent, string collectionName, string kind, bool unified)
        {
            var collection = Property(owner, collectionName)!;
            foreach (var value in Items(collection))
            {
                var row = HmiEntry(value, parent, Name(value), kind, collection); yield return row;
                if (kind == "table") foreach (var tag in HmiCollection(value, "Tags"))
                    foreach (var child in HmiTagRows(tag, row.Path + "/tags", unified, 0)) yield return child;
                if (kind == "screen" && unified)
                {
                    foreach (var binding in HmiDynamics(value, row.Path)) yield return binding;
                    if (HmiHas(value, "ScreenItems")) foreach (var item in HmiCollection(value, "ScreenItems"))
                    {
                        var child = HmiEntry(item, row.Path + "/items", Name(item), "screenItem"); yield return child;
                        foreach (var binding in HmiDynamics(item, child.Path)) yield return binding;
                    }
                }
                if (kind == "connection" && unified && HmiHas(value, "DriverProperties"))
                {
                    int index = 0;
                    foreach (var prop in HmiCollection(value, "DriverProperties")) yield return HmiEntry(prop, row.Path + "/driverProperties", (index++).ToString(System.Globalization.CultureInfo.InvariantCulture), "driverProperty");
                }
            }
        }
        private static IEnumerable<HmiRow> HmiTagRows(object tag, string parent, bool unified, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("HMI tag hierarchy exceeds depth 64.");
            var row = HmiEntry(tag, parent, Name(tag), "tag"); yield return row;
            if (unified && HmiHas(tag, "Members")) foreach (var child in HmiCollection(tag, "Members"))
                foreach (var member in HmiTagRows(child, row.Path + "/members", true, depth + 1)) yield return member;
        }
        private static IEnumerable<HmiRow> HmiDynamics(object value, string path)
        {
            if (!HmiHas(value, "Dynamizations")) yield break;
            int index = 0;
            foreach (var binding in HmiCollection(value, "Dynamizations")) yield return HmiEntry(binding, path + "/dynamizations", (index++).ToString(System.Globalization.CultureInfo.InvariantCulture), "dynamicBinding");
        }
        private static IEnumerable<HmiRow> HmiEnumerate(object software, string area)
        {
            bool unified = HmiUnified(software);
            if (area == "screen" || area == "all")
            {
                if (unified)
                {
                    foreach (var row in HmiObjects(software, "screens", "Screens", "screen", true)) yield return row;
                    foreach (var group in HmiCollection(software, "ScreenGroups")) foreach (var row in HmiFolder(group, "screens", "Screens", "screen", true, 0)) yield return row;
                }
                else
                {
                    foreach (var spec in new[] { new[] { "ScreenFolder", "screens", "Screens" }, new[] { "ScreenPopupFolder", "popups", "ScreenPopups" }, new[] { "ScreenTemplateFolder", "templates", "ScreenTemplates" } })
                    {
                        if (!HmiHas(software, spec[0])) continue;
                        var folder = Property(software, spec[0]);
                        if (folder != null) foreach (var row in HmiFolder(folder, spec[1], spec[2], "screen", false, 0)) yield return row;
                    }
                    var slideins = HmiHas(software, "ScreenSlideinFolder") ? Property(software, "ScreenSlideinFolder") : null;
                    if (slideins != null) foreach (var screen in HmiCollection(slideins, "ScreenSlideins"))
                        yield return HmiEntry(screen, "slideins", Convert.ToString(Property(screen, "SlideinType"))!, "screen");
                    foreach (var spec in new[] { new[] { "ScreenOverview", "overview" }, new[] { "ScreenGlobalElements", "globalElements" } })
                    { if (HmiHas(software, spec[0])) { var obj = Property(software, spec[0]); if (obj != null) yield return HmiEntry(obj, "special", spec[1], "screen"); } }
                }
            }
            if (area == "table" || area == "all")
            {
                if (unified)
                {
                    foreach (var row in HmiObjects(software, "tables", "TagTables", "table", true)) yield return row;
                    foreach (var group in HmiCollection(software, "TagTableGroups")) foreach (var row in HmiFolder(group, "tables", "TagTables", "table", true, 0)) yield return row;
                }
                else foreach (var row in HmiFolder(Property(software, "TagFolder")!, "tables", "TagTables", "table", false, 0)) yield return row;
            }
            if (area == "connection" || area == "all") foreach (var row in HmiObjects(software, "connections", "Connections", "connection", unified)) yield return row;
        }
        private static List<HmiRow> HmiRows(object software, string area)
        {
            var rows = HmiEnumerate(software, area).Take(100001).ToList();
            if (rows.Count > 100000) throw new InvalidOperationException("HMI scan exceeds 100000 records.");
            if (rows.GroupBy(r => r.Path, StringComparer.Ordinal).Any(g => g.Count() != 1)) throw new InvalidOperationException("Ambiguous canonical HMI paths.");
            return rows;
        }
        internal static bool HmiContentSupported(object value) => new[] { "Screen", "ScreenTemplate", "ScreenGlobalElements", "ScreenPopup", "ScreenOverview", "ScreenSlidein" }
            .Any(n => HmiIs(value, "Siemens.Engineering.Hmi.Screen." + n)) && value.GetType().GetMethod("Export", new[] { typeof(FileInfo), Type("ExportOptions") }) != null;
        internal static void HmiExport(object value, FileInfo file, string option) => Invoke(value, "Export", file, Enum.Parse(Type("ExportOptions"), option));
        private static Dictionary<string, string> HmiCapabilities(object software)
        {
            var capabilities = new Dictionary<string, string> {
            ["screens"] = "V19 public screen/folder collections; Classic optional categories are listed individually below.",
            ["screenItems"] = HmiClassic(software) ? "Classic XML through GetHmiScreenContent." : "Public ScreenItems when present in the installed V19 SDK; missing members are reported per record.",
            ["dynamicBindings"] = HmiClassic(software) ? "Serialized Classic XML bindings." : "Public Dynamizations when present; no private implementation traversal.",
            ["tagTables"] = "Public tables/tags; Unified Members when present.", ["connections"] = "Configured engineering connections and advertised attributes, not online state.",
            ["editing"] = HmiClassic(software) ? "Existing screens/templates/global elements/tags/connections; XML import only screens/templates/global elements." : "Unified editing excluded, same restriction as V21.",
            ["runtimeValues"] = "Unavailable: engineering configuration only.", ["pagination"] = "100000 records, depth 64; offset/limit is not a project snapshot." };
            if (HmiClassic(software))
                foreach (string member in new[] { "ScreenFolder", "ScreenPopupFolder", "ScreenTemplateFolder", "ScreenSlideinFolder", "ScreenOverview", "ScreenGlobalElements" })
                    capabilities[member] = !HmiHas(software, member) ? "UnverifiedMember: selected V19 SDK does not expose " + member :
                        Property(software, member) == null ? "UnsupportedByTarget: this target has no object for " + member : "Supported: public member present";
            return capabilities;
        }
        private static object? HmiProjectValue(object? value, int depth = 0)
        {
            if (value == null) return null;
            if (depth > 8) throw new NotSupportedException("Attribute nesting exceeds 8 levels.");
            var type = value.GetType();
            if (value is double d && (double.IsNaN(d) || double.IsInfinity(d)) || value is float f && (float.IsNaN(f) || float.IsInfinity(f))) throw new NotSupportedException("Non-finite value.");
            if (type.IsEnum || value is char || value is Guid || value is Version || value is Uri) return value.ToString();
            if (HmiScalar(type)) return value;
            if (value is DateTime date) return date.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (value is DateTimeOffset dateOffset) return dateOffset.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (value is TimeSpan span) return span.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            if (value is System.Globalization.CultureInfo culture) return culture.Name;
            if (value is FileSystemInfo file) return file.FullName;
            if (HmiIs(value, "Siemens.Engineering.MultilingualText"))
            {
                var texts = new Dictionary<string, object?>();
                foreach (var item in HmiCollection(value, "Items"))
                {
                    if (texts.Count >= 256) throw new NotSupportedException("Multilingual text exceeds 256 entries.");
                    texts.Add((string)Property(Property(Property(item, "Language")!, "Culture")!, "Name")!, Property(item, "Text"));
                }
                return texts;
            }
            if (HmiIs(value, "Siemens.Engineering.Language")) return Property(Property(value, "Culture")!, "Name");
            if (Type("IEngineeringObject").IsInstanceOfType(value))
            {
                var name = HmiAttribute(value, "Name") as string;
                if (string.IsNullOrWhiteSpace(name)) throw new NotSupportedException("Engineering reference has no readable Name.");
                return new Dictionary<string, object?> { ["Name"] = name, ["Type"] = type.FullName };
            }
            if (value is System.Collections.IDictionary dictionary)
            {
                var mapped = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                { if (mapped.Count >= 256 || !(entry.Key is string key)) throw new NotSupportedException("Attribute dictionary limit/type."); mapped.Add(key, HmiProjectValue(entry.Value, depth + 1)); }
                return mapped;
            }
            if (value is System.Collections.IList list || HmiIs(value, "Siemens.Engineering.HW.DeviceItemAssociation"))
            {
                var mapped = new List<object?>();
                foreach (var item in Items(value)) { if (mapped.Count >= 256) throw new NotSupportedException("Attribute collection exceeds 256 items."); mapped.Add(HmiProjectValue(item, depth + 1)); }
                return mapped;
            }
            throw new NotSupportedException("Public value type not projected: " + type.FullName);
        }
        private static object HmiReadAttribute(string name, string access, Func<object?> read, List<string> diagnostics)
        {
            try
            {
                var value = read(); object? scalar = HmiProjectValue(value);
                return new { Name = name, AccessMode = access, Value = scalar, ValueType = value?.GetType().FullName, Succeeded = true, Status = "Success", Error = "" };
            }
            catch (Exception ex) { diagnostics.Add(name + ": " + ex.Message); return new { Name = name, AccessMode = access, Value = (object?)null, ValueType = (string?)null, Succeeded = false, Status = "ReadFailed", Error = ex.Message }; }
        }
        private static V19HmiReadRecord HmiProject(HmiRow row)
        {
            var record = new V19HmiReadRecord { path = row.Path, parentPath = row.Parent, name = row.Name, kind = row.Kind, type = row.Object.GetType().FullName! };
            if (row.Kind == "screen" || row.Kind == "folder" || row.Kind == "screenItem" || row.Kind == "dynamicBinding")
                record.contentExport = HmiContentSupported(row.Object) ? "Available: public V19 XML Export via GetHmiScreenContent, subject to target restrictions." : "Unavailable for this record type.";
            try { foreach (var info in HmiInfos(row.Object))
                record.attributes.Add(HmiReadAttribute(info.Name, info.AccessMode, () => info.Read ? HmiAttribute(row.Object, info.Name) : throw new NotSupportedException("Attribute is not readable."), record.diagnostics)); }
            catch (Exception ex) { record.diagnostics.Add(ex.GetType().FullName + ": " + ex.Message); }
            if (row.Object.GetType().FullName!.Contains("HmiUnified"))
            {
                string[] props = row.Kind == "tag" ? new[] { "Address", "Connection", "DataType", "HmiDataType", "PlcTag", "PlcName", "TagTableName", "TagType" }
                    : row.Kind == "connection" ? new[] { "CommunicationDriver", "Partner", "Station", "Node", "InitialAddress", "DisabledAtStartup" }
                    : row.Kind == "driverProperty" ? new[] { "PropertyName", "Value", "Info" }
                    : row.Kind == "dynamicBinding" ? new[] { "PropertyName", "DynamizationType", "Tag", "Address", "PlcTag", "DataType", "ReadOnly", "UseIndirectAddressing", "ResourceList", "DynamicTagName", "ScriptCode", "GlobalDefinitionAreaScriptCode", "Async" } : Array.Empty<string>();
                foreach (var prop in props.Where(p => HmiHas(row.Object, p))) record.properties.Add(HmiReadAttribute(prop, "Read", () => Property(row.Object, prop), record.diagnostics));
                if (row.Kind == "dynamicBinding" && HmiHas(row.Object, "Trigger"))
                    foreach (var prop in new[] { "Type", "Tags", "CustomDuration" }) record.properties.Add(HmiReadAttribute("Trigger" + prop, "Read", () => Property(Property(row.Object, "Trigger")!, prop), record.diagnostics));
                foreach (var prop in row.Kind == "screen" ? new[] { "ScreenItems", "Dynamizations" } : row.Kind == "screenItem" ? new[] { "Dynamizations" } : row.Kind == "tag" ? new[] { "Members" } : row.Kind == "connection" ? new[] { "DriverProperties" } : Array.Empty<string>())
                    if (!HmiHas(row.Object, prop))
                    {
                        record.properties.Add(new { Name = prop, AccessMode = "Read", Value = (object?)null, ValueType = (string?)null, Status = "UnavailableInInstalledV19PublicApi", Succeeded = false, Error = "No public V19 member on this record." });
                        record.diagnostics.Add(prop + ": unavailable in installed V19 public API; child traversal is incomplete.");
                    }
            }
            return record;
        }
        private static V19HmiReadResult HmiRead(string softwarePath, string requested, int offset, int limit, string area)
        {
            var result = new V19HmiReadResult { softwarePath = softwarePath, requestedPath = requested, offset = offset, limit = limit };
            lock (Gate) try
            {
                if (offset < 0 || offset > 100000 || limit < 1 || limit > 500) throw new ArgumentOutOfRangeException("offset/limit");
                HmiValidatePath(requested, true);
                List<HmiRow> rows;
                if (area == "software")
                {
                    var inventory = Inventory().Where(e => e.kind == "software" && (HmiClassic(e.Target) || HmiUnified(e.Target))).ToList();
                    if (inventory.Any(e => e.ambiguous)) throw new InvalidOperationException("Ambiguous HMI software path.");
                    rows = inventory.Select(e => { HmiValidatePath(e.path); var p = e.path.LastIndexOf('/'); return HmiEntry(e.Target, p < 0 ? "" : e.path.Substring(0, p), p < 0 ? e.path : e.path.Substring(p + 1), "software"); }).ToList();
                }
                else
                {
                    var software = HmiSoftware(softwarePath); result.resolvedSoftwarePath = softwarePath; result.softwareType = software.GetType().FullName!; result.capabilities = HmiCapabilities(software);
                    rows = HmiRows(software, area);
                    if (area == "screen" && requested.Length == 0)
                        result.diagnostics.AddRange(result.capabilities.Where(c => c.Value.StartsWith("UnverifiedMember:", StringComparison.Ordinal)).Select(c => c.Value));
                }
                if (requested.Length > 0)
                {
                    if (!rows.Any(r => r.Path == requested && r.Kind == area)) throw new ArgumentException("Exact " + area + " path not found.");
                    rows = rows.Where(r => r.Path == requested || r.Path.StartsWith(requested + "/", StringComparison.Ordinal)).ToList();
                }
                result.records = rows.Skip(offset).Take(limit).Select(HmiProject).ToList();
                if (area == "software") foreach (var record in result.records)
                {
                    record.properties.Add(HmiReadAttribute("softwarePath", "Read", () => record.path, record.diagnostics));
                    record.properties.Add(HmiReadAttribute("alternateSoftwarePath", "Read", () => record.path, record.diagnostics));
                    record.properties.Add(HmiReadAttribute("publicSoftwareType", "Read", () => record.type, record.diagnostics));
                    record.properties.Add(HmiReadAttribute("capabilities", "Read", () => HmiCapabilities(rows.Single(r => r.Path == record.path).Object), record.diagnostics));
                }
                result.hasMore = rows.Count > offset + result.records.Count; result.nextOffset = result.hasMore ? (int?)(offset + result.records.Count) : null;
                result.Succeeded = result.records.All(r => r.diagnostics.Count == 0); result.complete = result.Succeeded && !result.hasMore && result.diagnostics.Count == 0;
                result.Meta = new JsonObject { ["success"] = result.Succeeded, ["partial"] = !result.Succeeded || result.diagnostics.Count != 0, ["pageComplete"] = result.Succeeded, ["hasMore"] = result.hasMore, ["diagnosticCount"] = result.records.Sum(r => r.diagnostics.Count) + result.diagnostics.Count };
                result.Message = "V19 engineering configuration page; inspect per-record capabilities and diagnostics.";
            }
            catch (Exception ex) { result.diagnostics.Add(ex.GetType().FullName + ": " + ex.Message); result.Meta = new JsonObject { ["success"] = false, ["partial"] = result.records.Count != 0, ["pageComplete"] = false }; result.Message = "HMI read failed or was incomplete."; }
            return result;
        }
        [McpServerTool(Name = "GetHmiSoftware"), Description("Discover Classic and Unified V19 HMI software with exact canonical paths. Read-only.")]
        public static V19HmiReadResult GetHmiSoftware(int offset = 0, int limit = 100) => HmiRead("", "", offset, limit, "software");
        [McpServerTool(Name = "GetHmiScreens"), Description("Read V19 HMI screens and folders; Unified public items/dynamizations when present.")]
        public static V19HmiReadResult GetHmiScreens(string softwarePath, string screenPath = "", int offset = 0, int limit = 100) => HmiRead(softwarePath, screenPath, offset, limit, "screen");
        [McpServerTool(Name = "GetHmiTagTables"), Description("Read configured V19 HMI tables, tags and public structured members.")]
        public static V19HmiReadResult GetHmiTagTables(string softwarePath, string tablePath = "", int offset = 0, int limit = 100) => HmiRead(softwarePath, tablePath, offset, limit, "table");
        [McpServerTool(Name = "GetHmiConnections"), Description("Read configured V19 HMI connections and public driver properties; no online sessions.")]
        public static V19HmiReadResult GetHmiConnections(string softwarePath, int offset = 0, int limit = 100) => HmiRead(softwarePath, "", offset, limit, "connection");
        [McpServerTool(Name = "GetHmiScreenContent"), Description("Read Classic V19 XML content in bounded immutable snapshot pages; positive offset requires prior sha256.")]
        public static object GetHmiScreenContent(string softwarePath, string screenPath, int offset = 0, int maxChars = 20000, string? expectedSha256 = null)
        {
            lock (Gate) try
            {
                var software = HmiSoftware(softwarePath); HmiValidatePath(screenPath);
                var rows = HmiRows(software, "screen"); var canonical = V19HmiContentPathResolution.Resolve(rows.Select(r => r.Path), screenPath);
                var row = rows.Single(r => r.Path == canonical);
                var page = (V19HmiScreenContent.ContentPage)V19HmiScreenContent.Read(row.Object, row.Name, offset, maxChars, expectedSha256, Convert.ToString(Property(Project(), "Path")) + "\n" + softwarePath + "\n" + canonical);
                var result = JsonSerializer.SerializeToNode(page)!.AsObject(); result["Succeeded"] = page.success; result["Meta"] = new JsonObject { ["success"] = page.success }; result["softwarePath"] = softwarePath; result["screenPath"] = canonical; result["requestedPath"] = screenPath; return result;
            }
            catch (Exception ex) { return new V19HmiReadResult { softwarePath = softwarePath, requestedPath = screenPath, offset = offset, limit = maxChars, diagnostics = new List<string> { ex.GetType().FullName + ": " + ex.Message }, Message = "HMI content read failed." }; }
        }
    }
}
