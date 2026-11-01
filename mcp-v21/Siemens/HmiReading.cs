using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HmiUnified;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using HmiAttribute = TiaMcpServer.ModelContextProtocol.Attribute;
using ClassicScreen = Siemens.Engineering.Hmi.Screen;
using ClassicTag = Siemens.Engineering.Hmi.Tag;
using UnifiedScreen = Siemens.Engineering.HmiUnified.UI.Screens;
using UnifiedTag = Siemens.Engineering.HmiUnified.HmiTags;

namespace TiaMcpServer.Siemens
{
    public sealed class HmiReadRecord
    {
        public string path { get; set; } = "";
        public string parentPath { get; set; } = "";
        public string name { get; set; } = "";
        public string kind { get; set; } = "";
        public string type { get; set; } = "";
        public string contentExport { get; set; } = "Not applicable to this record.";
        public List<HmiAttribute> attributes { get; set; } = new List<HmiAttribute>();
        public List<HmiAttribute> properties { get; set; } = new List<HmiAttribute>();
        public List<string> diagnostics { get; set; } = new List<string>();
    }

    public sealed class HmiReadResult
    {
        public bool Succeeded { get; set; }
        public JsonObject Meta { get; set; } = new JsonObject { ["success"] = false };
        public string Message { get; set; } = "";
        public string softwarePath { get; set; } = "";
        public string requestedPath { get; set; } = "";
        public string resolvedSoftwarePath { get; set; } = "";
        public string softwareType { get; set; } = "";
        public Dictionary<string, string> capabilities { get; set; } = new Dictionary<string, string>();
        public List<HmiReadRecord> records { get; set; } = new List<HmiReadRecord>();
        public List<string> diagnostics { get; set; } = new List<string>();
        public int offset { get; set; }
        public int limit { get; set; }
        public bool hasMore { get; set; }
        public int? nextOffset { get; set; }
        public bool complete { get; set; }
    }

    // All traversal uses documented V21 APIs. Classic content uses a private temporary
    // read-only export; no online sessions or project mutations are performed.
    public static class HmiReading
    {
        private const int MaxScan = 100000;
        private const int MaxDepth = 64;
        private sealed class SoftwareEntry
        {
            public Software Software = null!;
            public string Path = "";
            public string Alias = "";
        }
        private sealed class Row
        {
            public IEngineeringObject Object = null!;
            public string Path = "";
            public string Parent = "";
            public string Name = "";
            public string Kind = "";
            public Action<HmiReadRecord>? Details;
        }

        public static void ValidatePage(int offset, int limit)
        {
            if (offset < 0 || offset > MaxScan) throw new ArgumentOutOfRangeException(nameof(offset), "offset must be 0 to 100000.");
            if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit), "limit must be 1 to 500.");
        }

        // Preserve names verbatim, including whitespace and regex punctuation. Slash is
        // the path separator, never normalize, trim, decode or interpret input as a regex.
        public static void ValidatePath(string path, bool allowEmpty = false)
        {
            if (path == null || (!allowEmpty && path.Length == 0) || path.Any(char.IsControl) ||
                (path.Length != 0 && path.Split('/').Any(p => p.Length == 0 || p == "." || p == "..")))
                throw new ArgumentException("Use an exact slash-separated path returned by the HMI tools; empty/traversal segments and control characters are not allowed.");
        }

        private static string Join(string parent, string name)
        {
            if (name.Length == 0 || name.Contains("/") || name == "." || name == ".." || name.Any(char.IsControl))
                throw new InvalidOperationException("An engineering name cannot be represented unambiguously as a slash-separated path: " + name);
            return parent.Length == 0 ? name : parent + "/" + name;
        }
        private static void CheckDepth(int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("HMI hierarchy exceeds the supported depth of 64.");
        }
        private static Row Entry(IEngineeringObject obj, string parent, string name, string kind, Action<HmiReadRecord>? details = null)
            => new Row { Object = obj, Path = Join(parent, name), Parent = parent, Name = name, Kind = kind, Details = details };

        public static HmiReadResult GetHmiSoftware(Portal portal, int offset = 0, int limit = 100)
        {
            var result = NewResult("", "", offset, limit);
            try
            {
                ValidatePage(offset, limit);
                var entries = Inventory(portal);
                Page(entries.Select(e => Entry(e.Software, Parent(e.Path), e.Path.Substring(Parent(e.Path).Length + (Parent(e.Path).Length == 0 ? 0 : 1)), "software", r =>
                {
                    r.path = e.Path;
                    Property(r, "softwarePath", () => e.Path);
                    Property(r, "alternateSoftwarePath", () => e.Alias);
                    Property(r, "publicSoftwareType", () => PublicType(e.Software));
                    Property(r, "capabilities", () => Capabilities(e.Software));
                })), result, "", "");
                Finish(result);
            }
            catch (Exception ex) { Fail(result, ex); }
            return result;
        }

        public static HmiReadResult GetHmiScreens(Portal portal, string softwarePath, string screenPath = "", int offset = 0, int limit = 100)
            => Read(portal, softwarePath, screenPath, offset, limit, "screen", s => Screens(s));
        public static HmiReadResult GetHmiTagTables(Portal portal, string softwarePath, string tablePath = "", int offset = 0, int limit = 100)
            => Read(portal, softwarePath, tablePath, offset, limit, "table", s => Tables(s));
        public static HmiReadResult GetHmiConnections(Portal portal, string softwarePath, int offset = 0, int limit = 100)
            => Read(portal, softwarePath, "", offset, limit, "connection", s => Connections(s));

        public static object GetHmiScreenContent(Portal portal, string softwarePath, string screenPath, int offset = 0, int maxChars = 20000, string? expectedSha256 = null)
        {
            var failure = NewResult(softwarePath, screenPath, offset, maxChars);
            try
            {
                ValidatePath(softwarePath); ValidatePath(screenPath);
                var targets = Inventory(portal).Where(e => e.Path == softwarePath || e.Alias == softwarePath).ToList();
                if (targets.Count != 1) throw new InvalidOperationException("Use the exact unique softwarePath returned by GetHmiSoftware.");
                failure.resolvedSoftwarePath = targets[0].Path;
                failure.softwareType = PublicType(targets[0].Software);
                failure.capabilities = Capabilities(targets[0].Software);
                var screens = Screens(targets[0].Software).Take(MaxScan + 1).ToList();
                if (screens.Count > MaxScan) throw new InvalidOperationException("Screen inventory exceeds scan limit.");
                string canonical = HmiContentPathResolution.Resolve(screens.Select(row => row.Path), screenPath);
                var match = screens.Single(row => row.Path == canonical);
                if (!ClassicScreenContent.Supports(match.Object))
                    throw new NotSupportedException("HMI path exists: '" + canonical + "' (kind=" + match.Kind + ", type=" + match.Object.GetType().FullName +
                        "), but Classic XML content export is unavailable for this record. Use GetHmiScreens with an empty screenPath to browse public attributes; select an export-capable screen record for GetHmiScreenContent.");
                var page = (ClassicScreenContent.ContentPage)ClassicScreenContent.Read(match.Object, match.Name, offset, maxChars, expectedSha256, portal.GetState().ProjectPath + "\n" + targets[0].Path + "\n" + canonical);
                var content = System.Text.Json.JsonSerializer.SerializeToNode(page)!.AsObject();
                content["Succeeded"] = page.success;
                content["Meta"] = new JsonObject { ["success"] = page.success };
                content["softwarePath"] = targets[0].Path;
                content["screenPath"] = canonical;
                content["requestedPath"] = screenPath;
                return content;
            }
            catch (Exception ex) { Fail(failure, ex); return failure; }
        }

        private static HmiReadResult NewResult(string softwarePath, string requested, int offset, int limit)
            => new HmiReadResult { softwarePath = softwarePath, requestedPath = requested, offset = offset, limit = limit };
        private static HmiReadResult Read(Portal portal, string path, string requested, int offset, int limit, string kind, Func<Software, IEnumerable<Row>> rows)
        {
            var result = NewResult(path, requested, offset, limit);
            try
            {
                ValidatePage(offset, limit);
                ValidatePath(path);
                ValidatePath(requested, true);
                var matches = Inventory(portal).Where(e => e.Path == path || e.Alias == path).ToList();
                if (matches.Count != 1) throw new InvalidOperationException("Exact softwarePath must resolve to one HMI software; found " + matches.Count + ". Use GetHmiSoftware paths and exact case.");
                var target = matches[0];
                result.resolvedSoftwarePath = target.Path;
                result.softwareType = PublicType(target.Software);
                result.capabilities = Capabilities(target.Software);
                Page(rows(target.Software), result, requested, kind);
                Finish(result);
            }
            catch (Exception ex) { Fail(result, ex); }
            return result;
        }
        private static string Parent(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path.Substring(0, slash);
        }
        private static string PublicType(Software software) => software is HmiTarget ? typeof(HmiTarget).FullName! : typeof(HmiSoftware).FullName!;
        private static Dictionary<string, string> Capabilities(Software software)
        {
            bool unified = software is HmiSoftware;
            return new Dictionary<string, string>
            {
                ["screens"] = unified ? "Available: screens and screen groups; flattened child records." : "Available: normal, popup, template, slide-in, overview and global-element screens and folders.",
                ["screenItems"] = unified ? "Available: HmiScreen.ScreenItems and public item attributes. Embedded faceplate internals and nested control-specific collections are not expanded." : "Available through GetHmiScreenContent serialized XML for Classic screens, templates, popups, slide-ins, overview and global elements; this hierarchy query does not expand controls.",
                ["dynamicBindings"] = unified ? "Available: UIBase.Dynamizations with public attributes and typed tag/resource/script binding properties. Value-converter internals are not expanded." : "Available as serialized binding definitions through GetHmiScreenContent for Classic screens, templates, popups, slide-ins, overview and global elements; external tags/connections require their dedicated queries.",
                ["tagTables"] = unified ? "Available: table groups, tables, tags and structured members; address, connection and PLC mappings." : "Available: folders, tables and tags; address/connection data only as advertised by GetAttributeInfos.",
                ["connections"] = unified ? "Available: configured driver, partner, station, node, initial address and driver properties, including S7/OPC UA when configured." : "Available: configured connections and advertised public attributes. Driver/protocol/partner are unavailable if not advertised by this target.",
                ["runtimeValues"] = "Unavailable: engineering configuration only; no S7/OPC UA online session.",
                ["pagination"] = "Offset/limit over flattened SDK enumeration order, at most 100000 scanned records and depth 64. Not a snapshot; keep engineering edits paused between pages."
            };
        }

        private static List<SoftwareEntry> Inventory(Portal portal)
        {
            if (!portal.IsConnected() || !portal.ProjectIsValid) throw new InvalidOperationException("An existing selected project is required; this tool does not connect or open projects.");
            var state = portal.GetState();
            var projects = portal.IsLocalSession ? portal.GetSessions() : portal.GetProjects();
            var selected = projects.Where(p => p.Name == state.Project).ToList();
            if (selected.Count != 1) throw new InvalidOperationException("Cannot uniquely verify the active project.");
            var entries = EngineeringPaths.Inventory(selected[0])
                .Where(p => p.Kind == "software")
                .Select(p => new SoftwareEntry { Software = ((SoftwareContainer)p.Target).Software, Path = p.Path, Alias = p.Path })
                .Where(p => p.Software is HmiTarget || p.Software is HmiSoftware).ToList();
            if (entries.GroupBy(e => e.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Ambiguous canonical HMI software paths in the selected project.");
            return entries;
        }
        private static void Visit(ref int visited, int depth)
        {
            CheckDepth(depth);
            if (++visited > MaxScan) throw new InvalidOperationException("HMI discovery exceeded 100000 hardware objects; inventory is incomplete.");
        }
        private static void Groups(DeviceUserGroupComposition groups, string prefix, List<SoftwareEntry> entries, ref int visited, int depth)
        {
            foreach (var group in groups)
            {
                Visit(ref visited, depth);
                string path = Join(prefix, group.Name);
                Devices(group.Devices, path, entries, ref visited, depth + 1);
                Groups(group.Groups, path, entries, ref visited, depth + 1);
            }
        }
        private static void Devices(DeviceComposition devices, string prefix, List<SoftwareEntry> entries, ref int visited, int depth)
        {
            foreach (var device in devices)
            {
                Visit(ref visited, depth);
                foreach (var item in device.DeviceItems)
                    Item(item, Join(prefix, device.Name), prefix, entries, ref visited, depth + 1);
            }
        }
        private static void Item(DeviceItem item, string prefix, string aliasPrefix, List<SoftwareEntry> entries, ref int visited, int depth)
        {
            Visit(ref visited, depth);
            string path = Join(prefix, item.Name), alias = Join(aliasPrefix, item.Name);
            var software = item.GetService<SoftwareContainer>()?.Software;
            if (software is HmiTarget || software is HmiSoftware)
                entries.Add(new SoftwareEntry { Software = software, Path = path, Alias = alias });
            foreach (var child in item.DeviceItems) Item(child, path, alias, entries, ref visited, depth + 1);
        }

        private static void Property(HmiReadRecord record, string name, Func<object?> read)
            => record.properties.Add(AttributeProjection.Read(name, "Read", true, read, SiemensAttributeProjection.TryConvert));
        private static HmiReadRecord Project(Row row)
        {
            var record = new HmiReadRecord { path = row.Path, parentPath = row.Parent, name = row.Name, kind = row.Kind, type = row.Object.GetType().FullName ?? "" };
            if (row.Kind == "screen" || row.Kind == "folder" || row.Kind == "screenItem" || row.Kind == "dynamicBinding")
                record.contentExport = ClassicScreenContent.Supports(row.Object)
                    ? "Available: public V21 Export(FileInfo, ExportOptions) via GetHmiScreenContent; XML only, subject to target export restrictions."
                    : "Unavailable for this record type. GetHmiScreens exposes public attributes; Unified also exposes items and dynamizations.";
            try { record.attributes = Helper.GetAttributeList(row.Object); }
            catch (Exception ex) { record.diagnostics.Add(AttributeProjection.DescribeException(ex)); }
            try { row.Details?.Invoke(record); }
            catch (Exception ex) { record.diagnostics.Add(AttributeProjection.DescribeException(ex)); }
            foreach (var attribute in record.attributes.Concat(record.properties).Where(a => !a.Succeeded))
                record.diagnostics.Add(attribute.Name + ": " + attribute.Status + ": " + attribute.Error);
            return record;
        }
        private static void Page(IEnumerable<Row> rows, HmiReadResult result, string requested, string kind)
        {
            int scanned = 0, selected = 0;
            bool found = requested.Length == 0;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (++scanned > MaxScan) throw new InvalidOperationException("Enumeration exceeded 100000 records; result is incomplete.");
                if (!paths.Add(row.Path)) throw new InvalidOperationException("Ambiguous HMI record path: " + row.Path);
                if (requested.Length != 0)
                {
                    if (row.Path == requested && row.Kind == kind) found = true;
                    if (!found || (row.Path != requested && !row.Path.StartsWith(requested + "/", StringComparison.Ordinal))) continue;
                }
                if (selected++ < result.offset) continue;
                if (result.records.Count == result.limit)
                {
                    result.hasMore = true;
                    result.nextOffset = result.offset + result.records.Count;
                    break;
                }
                result.records.Add(Project(row));
            }
            if (!found) throw new InvalidOperationException("Exact " + kind + " path was not found: " + requested);
        }
        private static void Finish(HmiReadResult result)
        {
            int failures = result.records.Sum(r => r.diagnostics.Count);
            result.Succeeded = failures == 0 && result.diagnostics.Count == 0;
            result.complete = result.Succeeded && !result.hasMore;
            result.Meta = new JsonObject { ["success"] = result.Succeeded, ["partial"] = !result.Succeeded, ["pageComplete"] = result.Succeeded, ["hasMore"] = result.hasMore, ["diagnosticCount"] = failures + result.diagnostics.Count };
            result.Message = result.Succeeded ? "Engineering configuration page read. Check capabilities for unavailable details; complete means this selected traversal is exhausted, not that every SDK capability exists." : "Partial engineering configuration; inspect record and attribute diagnostics.";
        }
        private static void Fail(HmiReadResult result, Exception ex)
        {
            result.Succeeded = false;
            result.complete = false;
            result.diagnostics.Add(AttributeProjection.DescribeException(ex));
            result.Meta = new JsonObject { ["success"] = false, ["partial"] = result.records.Count != 0, ["pageComplete"] = false };
            result.Message = "HMI read failed or was incomplete; inspect diagnostics.";
        }

        private static IEnumerable<Row> Screens(Software software)
        {
            if (software is HmiTarget classic)
            {
                foreach (var row in ClassicScreens(classic.ScreenFolder, "screens", 0)) yield return row;
                foreach (var row in ClassicPopups(classic.ScreenPopupFolder, "popups", 0)) yield return row;
                foreach (var row in ClassicTemplates(classic.ScreenTemplateFolder, "templates", 0)) yield return row;
                foreach (var screen in classic.ScreenSlideinFolder.ScreenSlideins) yield return Entry(screen, "slideins", screen.SlideinType.ToString(), "screen");
                if (classic.ScreenOverview != null) yield return Entry(classic.ScreenOverview, "special", "overview", "screen");
                if (classic.ScreenGlobalElements != null) yield return Entry(classic.ScreenGlobalElements, "special", "globalElements", "screen");
            }
            else if (software is HmiSoftware unified)
            {
                foreach (var screen in unified.Screens) foreach (var row in UnifiedScreenRows(screen, "screens")) yield return row;
                foreach (var group in unified.ScreenGroups) foreach (var row in UnifiedScreens(group, "screens", 0)) yield return row;
            }
            else throw new NotSupportedException("Unsupported HMI software type.");
        }
        private static IEnumerable<Row> ClassicScreens(ClassicScreen.ScreenFolder folder, string parent, int depth)
        {
            CheckDepth(depth);
            var root = Entry(folder, parent, folder.Name, "folder"); yield return root;
            foreach (var screen in folder.Screens) yield return Entry(screen, root.Path, screen.Name, "screen");
            foreach (var child in folder.Folders) foreach (var row in ClassicScreens(child, root.Path, depth + 1)) yield return row;
        }
        private static IEnumerable<Row> ClassicPopups(ClassicScreen.ScreenPopupFolder folder, string parent, int depth)
        {
            CheckDepth(depth);
            var root = Entry(folder, parent, folder.Name, "folder"); yield return root;
            foreach (var screen in folder.ScreenPopups) yield return Entry(screen, root.Path, screen.Name, "screen");
            foreach (var child in folder.Folders) foreach (var row in ClassicPopups(child, root.Path, depth + 1)) yield return row;
        }
        private static IEnumerable<Row> ClassicTemplates(ClassicScreen.ScreenTemplateFolder folder, string parent, int depth)
        {
            CheckDepth(depth);
            var root = Entry(folder, parent, folder.Name, "folder"); yield return root;
            foreach (var screen in folder.ScreenTemplates) yield return Entry(screen, root.Path, screen.Name, "screen");
            foreach (var child in folder.Folders) foreach (var row in ClassicTemplates(child, root.Path, depth + 1)) yield return row;
        }
        private static IEnumerable<Row> UnifiedScreens(global::Siemens.Engineering.HmiUnified.UI.ScreenGroup.HmiScreenGroup group, string parent, int depth)
        {
            CheckDepth(depth);
            var root = Entry(group, parent, group.Name, "folder"); yield return root;
            foreach (var screen in group.Screens) foreach (var row in UnifiedScreenRows(screen, root.Path)) yield return row;
            foreach (var child in group.Groups) foreach (var row in UnifiedScreens(child, root.Path, depth + 1)) yield return row;
        }
        private static IEnumerable<Row> UnifiedScreenRows(UnifiedScreen.HmiScreen screen, string parent)
        {
            var root = Entry(screen, parent, screen.Name, "screen"); yield return root;
            foreach (var row in Dynamics(screen, root.Path)) yield return row;
            foreach (var item in screen.ScreenItems)
            {
                var entry = Entry(item, root.Path + "/items", item.Name, "screenItem"); yield return entry;
                foreach (var row in Dynamics(item, entry.Path)) yield return row;
            }
        }
        private static IEnumerable<Row> Dynamics(global::Siemens.Engineering.HmiUnified.UI.UIBase owner, string parent)
        {
            int index = 0;
            foreach (var binding in owner.Dynamizations)
            {
                yield return Entry(binding, parent + "/dynamizations", (index++).ToString(System.Globalization.CultureInfo.InvariantCulture), "dynamicBinding", record =>
                {
                    Property(record, "PropertyName", () => binding.PropertyName);
                    Property(record, "DynamizationType", () => binding.DynamizationType);
                    if (binding is global::Siemens.Engineering.HmiUnified.UI.Dynamization.TagDynamization tag)
                    {
                        Property(record, "Tag", () => tag.Tag);
                        Property(record, "Address", () => tag.Address);
                        Property(record, "PlcTag", () => tag.PlcTag);
                        Property(record, "DataType", () => tag.DataType);
                        Property(record, "ReadOnly", () => tag.ReadOnly);
                        Property(record, "UseIndirectAddressing", () => tag.UseIndirectAddressing);
                    }
                    if (binding is global::Siemens.Engineering.HmiUnified.UI.Dynamization.ResourceListDynamization resource)
                    {
                        Property(record, "Tag", () => resource.Tag);
                        Property(record, "ResourceList", () => resource.ResourceList);
                    }
                    if (binding is global::Siemens.Engineering.HmiUnified.UI.Dynamization.TagParameterDynamization parameter)
                        Property(record, "DynamicTagName", () => parameter.DynamicTagName);
                    if (binding is global::Siemens.Engineering.HmiUnified.UI.Dynamization.Script.ScriptDynamization script)
                    {
                        Property(record, "ScriptCode", () => script.ScriptCode);
                        Property(record, "GlobalDefinitionAreaScriptCode", () => script.GlobalDefinitionAreaScriptCode);
                        Property(record, "Async", () => script.Async);
                        Property(record, "TriggerType", () => script.Trigger.Type);
                        Property(record, "TriggerTags", () => script.Trigger.Tags);
                        Property(record, "TriggerCustomDuration", () => script.Trigger.CustomDuration);
                    }
                });
            }
        }

        private static IEnumerable<Row> Tables(Software software)
        {
            if (software is HmiTarget classic)
                foreach (var row in ClassicTables(classic.TagFolder, "tables", 0)) yield return row;
            else if (software is HmiSoftware unified)
            {
                foreach (var table in unified.TagTables) foreach (var row in UnifiedTable(table, "tables")) yield return row;
                foreach (var group in unified.TagTableGroups) foreach (var row in UnifiedTables(group, "tables", 0)) yield return row;
            }
            else throw new NotSupportedException("Unsupported HMI software type.");
        }
        private static IEnumerable<Row> ClassicTables(ClassicTag.TagFolder folder, string parent, int depth)
        {
            CheckDepth(depth);
            var root = Entry(folder, parent, folder.Name, "folder"); yield return root;
            foreach (var table in folder.TagTables)
            {
                var entry = Entry(table, root.Path, table.Name, "table"); yield return entry;
                foreach (var tag in table.Tags) yield return Entry(tag, entry.Path + "/tags", tag.Name, "tag");
            }
            foreach (var child in folder.Folders) foreach (var row in ClassicTables(child, root.Path, depth + 1)) yield return row;
        }
        private static IEnumerable<Row> UnifiedTables(UnifiedTag.HmiTagTableGroup group, string parent, int depth)
        {
            CheckDepth(depth);
            var root = Entry(group, parent, group.Name, "folder"); yield return root;
            foreach (var table in group.TagTables) foreach (var row in UnifiedTable(table, root.Path)) yield return row;
            foreach (var child in group.Groups) foreach (var row in UnifiedTables(child, root.Path, depth + 1)) yield return row;
        }
        private static IEnumerable<Row> UnifiedTable(UnifiedTag.HmiTagTable table, string parent)
        {
            var entry = Entry(table, parent, table.Name, "table"); yield return entry;
            foreach (var tag in table.Tags) foreach (var row in UnifiedTagRows(tag, entry.Path + "/tags", 0)) yield return row;
        }
        private static IEnumerable<Row> UnifiedTagRows(UnifiedTag.HmiTag tag, string parent, int depth)
        {
            CheckDepth(depth);
            var entry = Entry(tag, parent, tag.Name, "tag", record =>
            {
                Property(record, "Address", () => tag.Address);
                Property(record, "Connection", () => tag.Connection);
                Property(record, "DataType", () => tag.DataType);
                Property(record, "HmiDataType", () => tag.HmiDataType);
                Property(record, "PlcTag", () => tag.PlcTag);
                Property(record, "PlcName", () => tag.PlcName);
                Property(record, "TagTableName", () => tag.TagTableName);
                Property(record, "TagType", () => tag.TagType);
            });
            yield return entry;
            foreach (var child in tag.Members) foreach (var row in UnifiedTagRows(child, entry.Path + "/members", depth + 1)) yield return row;
        }
        private static IEnumerable<Row> Connections(Software software)
        {
            if (software is HmiTarget classic)
                foreach (var connection in classic.Connections) yield return Entry(connection, "connections", connection.Name, "connection");
            else if (software is HmiSoftware unified)
                foreach (var connection in unified.Connections)
                {
                    var entry = Entry(connection, "connections", connection.Name, "connection", record =>
                    {
                        Property(record, "CommunicationDriver", () => connection.CommunicationDriver);
                        Property(record, "Partner", () => connection.Partner);
                        Property(record, "Station", () => connection.Station);
                        Property(record, "Node", () => connection.Node);
                        Property(record, "InitialAddress", () => connection.InitialAddress);
                        Property(record, "DisabledAtStartup", () => connection.DisabledAtStartup);
                    });
                    yield return entry;
                    int index = 0;
                    foreach (var property in connection.DriverProperties)
                        yield return Entry(property, entry.Path + "/driverProperties", (index++).ToString(System.Globalization.CultureInfo.InvariantCulture), "driverProperty", record =>
                        {
                            Property(record, "PropertyName", () => property.PropertyName);
                            Property(record, "Value", () => property.Value);
                            Property(record, "Info", () => property.Info);
                        });
                }
            else throw new NotSupportedException("Unsupported HMI software type.");
        }
    }
}
