using ModelContextProtocol.Server;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.Compatibility
{
    public static partial class V19Tools
    {
        public sealed class PlcBackupFile
        {
            public string path { get; set; } = "";
            public long bytes { get; set; }
            public string sha256 { get; set; } = "";
            public bool readable { get; set; }
        }
        public sealed class PlcBackupEntry
        {
            public string blockPath { get; set; } = "";
            public bool Succeeded { get; set; }
            public string format { get; set; } = "";
            public string error { get; set; } = "";
            public List<PlcBackupFile> files { get; set; } = new List<PlcBackupFile>();
        }
        public sealed class PlcEditResult
        {
            public bool Succeeded { get; set; }
            public Dictionary<string, object> Meta { get; set; } = new Dictionary<string, object> { ["success"] = false };
            public string Message { get; set; } = "";
            public string softwarePath { get; set; } = "";
            public string blockPath { get; set; } = "";
            public string newBlockPath { get; set; } = "";
            public string backupPath { get; set; } = "";
            public string backupScope { get; set; } = "target";
            public bool complete { get; set; }
            public bool readable { get; set; }
            public bool mutationAttempted { get; set; }
            public bool postconditionVerified { get; set; }
            public int totalBlocks { get; set; }
            public List<PlcBackupEntry> backups { get; set; } = new List<PlcBackupEntry>();
            public List<string> blockers { get; set; } = new List<string>();
        }
        private sealed class PlcTarget
        {
            public object Value = null!;
            public string Path = "";
            public object? Modified;
            public bool Restricted;
        }
        private static object? PlcOptional(object target, string property)
            => target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
        private static void PlcLiteral(string path, string argument, bool empty = false)
        {
            if (empty && path == "") return;
            if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.IndexOfAny(new[] { '*', '?', '[', ']', '\\' }) >= 0 ||
                path.Any(char.IsControl) || path.Split('/').Any(s => s.Length == 0 || s == "." || s == ".."))
                throw new ArgumentException(argument + " must be an exact slash-separated path without wildcards or traversal.");
        }
        private static object PlcSoftware(string path)
        {
            PlcLiteral(path, "softwarePath");
            var software = Find("software", path);
            if (!Type("SW.PlcSoftware").IsInstanceOfType(software)) throw new ArgumentException("softwarePath must resolve to PLC software.");
            return software;
        }
        private static List<PlcTarget> PlcEnumerate(object software, bool allBlocks, bool types = false)
        {
            var rows = new List<PlcTarget>();
            void Walk(object group, string prefix, bool restricted, int depth, string collection)
            {
                if (depth > 64) throw new InvalidOperationException("PLC group hierarchy exceeds 64 levels.");
                foreach (var value in Items(Property(group, collection))) rows.Add(new PlcTarget { Value = value, Path = prefix + Name(value), Modified = Property(value, "ModifiedDate"), Restricted = restricted });
                foreach (var child in Items(Property(group, "Groups"))) Walk(child, prefix + Name(child) + "/", restricted, depth + 1, collection);
                var systems = allBlocks ? PlcOptional(group, "SystemBlockGroups") : null;
                if (systems != null) foreach (var child in Items(systems)) Walk(child, prefix + "@system/" + Name(child) + "/", true, depth + 1, "Blocks");
            }
            Walk(Property(software, types ? "TypeGroup" : "BlockGroup")!, "", false, 0, types ? "Types" : "Blocks");
            if (allBlocks && !types)
            {
                // V19 publicly exposes PlcUnitProvider; a failure to query it must not become an empty successful backup.
                var units = Service(software, "SW.Units.PlcUnitProvider");
                if (units != null)
                {
                    var group = Property(units, "UnitGroup")!;
                    foreach (var unit in Items(Property(group, "Units"))) Walk(Property(unit, "BlockGroup")!, "@units/" + Name(unit) + "/", false, 0, "Blocks");
                    var safety = PlcOptional(group, "SafetyUnits");
                    if (safety != null) foreach (var unit in Items(safety)) Walk(Property(unit, "BlockGroup")!, "@safetyUnits/" + Name(unit) + "/", true, 0, "Blocks");
                }
                Walk(Property(software, "TechnologicalObjectGroup")!, "@technology/", true, 0, "TechnologicalObjects");
            }
            if (rows.GroupBy(r => r.Path, StringComparer.Ordinal).Any(g => g.Count() > 1)) throw new InvalidOperationException("Ambiguous PLC paths in inventory.");
            return rows;
        }
        private static PlcTarget PlcResolve(List<PlcTarget> rows, object software, string path, bool types = false)
        {
            PlcLiteral(path, types ? "typePath" : "blockPath");
            var root = Name(Property(software, types ? "TypeGroup" : "BlockGroup")!) + "/";
            var matches = rows.Where(r => r.Path == path || root + r.Path == path).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Exact path must resolve to one PLC object; found " + matches.Length + ".");
            return matches[0];
        }
        private static object PlcGroup(object software, string path, bool types)
        {
            PlcLiteral(path, "groupPath", true);
            var root = Property(software, types ? "TypeGroup" : "BlockGroup")!;
            if (path == "" || path == Name(root)) return root;
            var prefix = Name(root) + "/";
            if (path.StartsWith(prefix, StringComparison.Ordinal)) path = path.Substring(prefix.Length);
            var current = root;
            foreach (var segment in path.Split('/'))
            {
                var matches = Items(Property(current, "Groups")).Where(g => Name(g) == segment).ToArray();
                if (matches.Length != 1) throw new ArgumentException("PLC group not found or ambiguous: " + path);
                current = matches[0];
            }
            return current;
        }
        private static Dictionary<string, object?> PlcInfo(PlcTarget row, bool types)
        {
            var value = row.Value;
            var result = new Dictionary<string, object?> {
                ["Name"] = Name(value), ["TypeName"] = value.GetType().Name, ["Namespace"] = PlcOptional(value, "Namespace"),
                ["IsConsistent"] = Property(value, "IsConsistent"), ["ModifiedDate"] = Property(value, "ModifiedDate"),
                ["IsKnowHowProtected"] = Property(value, "IsKnowHowProtected"), ["Description"] = value.ToString(), ["path"] = row.Path,
                ["Attributes"] = PlcAttributes(value)
            };
            if (!types) { result["ProgrammingLanguage"] = Convert.ToString(Property(value, "ProgrammingLanguage")); result["MemoryLayout"] = Convert.ToString(PlcOptional(value, "MemoryLayout")); result["HeaderName"] = PlcOptional(value, "HeaderName"); }
            return result;
        }
        public sealed class PlcAttribute
        {
            public string Name { get; set; } = "";
            public object? Value { get; set; }
            public string? AccessMode { get; set; }
            public string? ValueType { get; set; }
            public string Status { get; set; } = "NotRead";
            public bool Succeeded => Status == "Success";
            public string? Error { get; set; }
        }
        private static PlcAttribute[] PlcAttributes(object value)
        {
            // Do not serialize raw SDK objects or read write-only attributes.
            var contract = Type("IEngineeringObject");
            if (!contract.IsInstanceOfType(value)) return Array.Empty<PlcAttribute>();
            var infos = contract.GetMethod("GetAttributeInfos", System.Type.EmptyTypes)!.Invoke(value, null);
            var read = contract.GetMethod("GetAttribute", new[] { typeof(string) })!;
            return Items(infos).Select(info => {
                var attribute = new PlcAttribute { Name = Name(info), AccessMode = Convert.ToString(Property(info, "AccessMode")) };
                if (attribute.AccessMode == null || attribute.AccessMode.IndexOf("Read", StringComparison.OrdinalIgnoreCase) < 0)
                { attribute.Status = "NotReadable"; attribute.Error = "Attribute metadata does not permit reading."; return attribute; }
                object? raw;
                try { raw = read.Invoke(value, new object[] { attribute.Name }); attribute.ValueType = raw?.GetType().FullName; }
                catch (Exception ex) { attribute.Status = "ReadFailed"; attribute.Error = (ex.InnerException ?? ex).Message; return attribute; }
                try { attribute.Value = PlcProjectAttribute(raw, 0); attribute.Status = "Success"; }
                catch (Exception ex) { attribute.Status = "ProjectionFailed"; attribute.Error = ex.Message; }
                return attribute;
            }).ToArray();
        }
        private static object? PlcProjectAttribute(object? value, int depth)
        {
            if (value == null) return null;
            if (depth > 8) throw new NotSupportedException("Attribute nesting exceeds 8 levels.");
            if (value is string || value is bool || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal) return value;
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d)) return d;
            if (value is float f && !float.IsNaN(f) && !float.IsInfinity(f)) return f;
            if (value is Enum || value is char || value is Guid || value is Version || value is Uri) return value.ToString();
            if (value is DateTime date) return date.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (value is DateTimeOffset offset) return offset.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            if (value is TimeSpan span) return span.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            if (value is System.Globalization.CultureInfo culture) return culture.Name;
            if (value is FileSystemInfo file) return file.FullName;
            if (value is System.Collections.IDictionary dictionary)
            {
                var projected = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry item in dictionary)
                { if (projected.Count >= 256 || !(item.Key is string key)) throw new NotSupportedException("Attribute dictionary requires string keys and at most 256 items."); projected.Add(key, PlcProjectAttribute(item.Value, depth + 1)); }
                return projected;
            }
            if (value is System.Collections.IList list)
            {
                if (list.Count > 256) throw new NotSupportedException("Attribute collection exceeds 256 items.");
                return list.Cast<object?>().Select(item => PlcProjectAttribute(item, depth + 1)).ToArray();
            }
            throw new NotSupportedException("Attribute value type has no JSON-safe projection: " + value.GetType().FullName);
        }
        private static Dictionary<string, object> PlcInfoMetadata(IEnumerable<Dictionary<string, object?>> rows)
        {
            var failed = rows.SelectMany(r => (PlcAttribute[])r["Attributes"]!).Count(a => !a.Succeeded);
            var meta = new Dictionary<string, object> { ["success"] = failed == 0, ["timestamp"] = DateTime.Now, ["attributesComplete"] = failed == 0, ["attributeFailureCount"] = failed };
            if (failed != 0) meta["partial"] = true;
            return meta;
        }
        private static Regex PlcRegex(string pattern) => new Regex(pattern ?? "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        private static XDocument PlcReadXml(string path)
        {
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) return XDocument.Load(reader);
        }
        private static FileInfo PlcImportFile(string path, bool types)
        {
            var file = new FileInfo(path);
            if (!file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected SIMATIC ML .xml. SIMATIC SD .s7dcl/.s7res and plain SCL are different formats; renaming does not convert them.");
            if (!file.Exists || file.Length == 0) throw new IOException("Import file is missing or empty: " + file.FullName);
            var document = PlcReadXml(file.FullName);
            if (document.Root?.Name.LocalName != "Document" || !document.Descendants().Any(e => types ? e.Name.LocalName == "SW.Types.PlcStruct" : e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal)))
                throw new ArgumentException("Expected a SIMATIC ML Document containing " + (types ? "SW.Types.PlcStruct" : "SW.Blocks.*") + ".");
            return file;
        }
        private static object PlcImport(string softwarePath, string groupPath, string importPath, bool types)
        {
            lock (Gate)
            {
                var file = PlcImportFile(importPath, types);
                var group = PlcGroup(PlcSoftware(softwarePath), groupPath, types);
                var imported = Items(Invoke(Property(group, types ? "Types" : "Blocks")!, "Import", file, Enum.Parse(Type("ImportOptions"), "Override"))).ToArray();
                if (imported.Length == 0) throw new InvalidOperationException("Siemens returned no imported objects for " + file.FullName);
                return new { Message = (types ? "Type" : "Block") + " imported from '" + file.FullName + "' to '" + groupPath + "'", Succeeded = true, Meta = new { success = true, timestamp = DateTime.Now }, importedCount = imported.Length };
            }
        }
        [McpServerTool(Name = "ImportBlock"), Description("Import SIMATIC ML .xml into an exact PLC block group using V19 ImportOptions.Override. Does not compile or save. Not SIMATIC SD or SCL.")]
        public static object ImportBlock(string softwarePath, string groupPath, string importPath) => PlcImport(softwarePath, groupPath, importPath, false);
        [McpServerTool(Name = "ImportType"), Description("Import a SIMATIC ML PLC type .xml into an exact type group using V19 ImportOptions.Override. Does not compile or save.")]
        public static object ImportType(string softwarePath, string groupPath, string importPath) => PlcImport(softwarePath, groupPath, importPath, true);
        [McpServerTool(Name = "GetTypeInfo"), Description("Read a PLC type by exact relative path, optionally prefixed by the actual type root name.")]
        public static object GetTypeInfo(string softwarePath, string typePath)
        {
            lock (Gate) { var software = PlcSoftware(softwarePath); var result = PlcInfo(PlcResolve(PlcEnumerate(software, false, true), software, typePath, true), true); result["Message"] = "Type info retrieved"; result["Meta"] = PlcInfoMetadata(new[] { result }); return result; }
        }
        [McpServerTool(Name = "GetTypes"), Description("Read PLC user types in nested groups; regexName filters names, empty selects all.")]
        public static object GetTypes(string softwarePath, string regexName = "")
        {
            lock (Gate) { var rx = PlcRegex(regexName); var items = PlcEnumerate(PlcSoftware(softwarePath), false, true).Where(r => rx.IsMatch(Name(r.Value))).Select(r => PlcInfo(r, true)).ToArray(); return new { Message = "PLC types retrieved", Items = items, Meta = PlcInfoMetadata(items) }; }
        }
        private static string PlcExportFile(string root, string relative)
        {
            var directory = Path.GetFullPath(root);
            foreach (var segment in relative.Split('/'))
                if (segment == "." || segment == ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)) throw new ArgumentException("PLC name cannot be represented as an export file: " + relative);
            var full = Path.GetFullPath(Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar) + ".xml"));
            if (!full.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Export path escapes directory.");
            return full;
        }
        private static PlcBackupFile PlcInspect(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length == 0) throw new IOException("Export produced an empty file: " + path);
                using (var sha = SHA256.Create()) return new PlcBackupFile { path = Path.GetFullPath(path), bytes = stream.Length, sha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(), readable = true };
            }
        }
        private static void PlcExportXml(object value, string path, bool defaults = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // V21 exports replace the chosen destination. Export to a fresh sibling first so SDK failure preserves existing data.
            var temp = Path.Combine(Path.GetDirectoryName(path)!, ".v19-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                Invoke(value, "Export", new FileInfo(temp), Enum.Parse(Type("ExportOptions"), defaults ? "WithDefaults" : "None"));
                PlcReadXml(temp); PlcInspect(temp);
                if (File.Exists(path)) File.Copy(temp, path, true); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        [McpServerTool(Name = "ExportType"), Description("Export one consistent PLC type to an XML file under exportPath directory; preservePath includes the actual type root/group hierarchy.")]
        public static object ExportType(string softwarePath, string exportPath, string typePath, bool preservePath = false)
        {
            lock (Gate)
            {
                var software = PlcSoftware(softwarePath); var row = PlcResolve(PlcEnumerate(software, false, true), software, typePath, true);
                if (!Convert.ToBoolean(Property(row.Value, "IsConsistent"))) throw new InvalidOperationException("Type is inconsistent.");
                var file = PlcExportFile(exportPath, preservePath ? Name(Property(software, "TypeGroup")!) + "/" + row.Path : Name(row.Value));
                PlcExportXml(row.Value, file); return new { Message = "Type exported", ExportPath = file, Succeeded = true, Meta = new { success = true, timestamp = DateTime.Now } };
            }
        }
        private static object PlcExportMany(string softwarePath, string exportPath, string regexName, bool preservePath, bool types)
        {
            lock (Gate)
            {
                var start = DateTime.UtcNow; var software = PlcSoftware(softwarePath); var rx = PlcRegex(regexName);
                var rows = PlcEnumerate(software, false, types).Where(r => rx.IsMatch(Name(r.Value))).ToArray();
                var outputs = rows.Select(r => PlcExportFile(exportPath, preservePath ? Name(Property(software, types ? "TypeGroup" : "BlockGroup")!) + "/" + r.Path : Name(r.Value))).ToArray();
                if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) throw new ArgumentException("Export filenames collide; use preservePath=true or select unique names.");
                var exported = new List<object>(); var inconsistent = new List<object>(); var failures = new List<object>();
                for (var i = 0; i < rows.Length; i++)
                {
                    var row = rows[i];
                    if (!Convert.ToBoolean(Property(row.Value, "IsConsistent"))) { inconsistent.Add(PlcInfo(row, types)); continue; }
                    try { PlcExportXml(row.Value, outputs[i]); exported.Add(PlcInfo(row, types)); }
                    catch (Exception ex) { failures.Add(new { path = row.Path, error = ex.Message }); }
                }
                var meta = new Dictionary<string, object> { ["success"] = failures.Count == 0, ["timestamp"] = DateTime.Now, [types ? "totalTypes" : "totalBlocks"] = rows.Length, [types ? "exportedTypes" : "exportedBlocks"] = exported.Count, [types ? "inconsistentTypes" : "inconsistentBlocks"] = inconsistent.Count, ["duration"] = (DateTime.UtcNow - start).TotalSeconds };
                return new { Message = "XML export completed; inspect Inconsistent and Failures before treating it as a complete backup.", Items = exported, Inconsistent = inconsistent, Failures = failures, Succeeded = failures.Count == 0, complete = failures.Count == 0 && inconsistent.Count == 0, Meta = meta };
            }
        }
        [McpServerTool(Name = "ExportBlocks"), Description("Export matching PLC user blocks as XML. Preserves V21 Items/Inconsistent and metadata counts; per-object failures are explicit. Not a full software backup.")]
        public static object ExportBlocks(string softwarePath, string exportPath, string regexName = "", bool preservePath = false) => PlcExportMany(softwarePath, exportPath, regexName, preservePath, false);
        [McpServerTool(Name = "ExportTypes"), Description("Export matching PLC types as XML, reporting Items, Inconsistent, failures and metadata counts.")]
        public static object ExportTypes(string softwarePath, string exportPath, string regexName = "", bool preservePath = false) => PlcExportMany(softwarePath, exportPath, regexName, preservePath, true);
        private static string PlcBackupDirectory(string parent)
        {
            var root = string.IsNullOrWhiteSpace(parent) ? "" : Path.GetPathRoot(parent) ?? "";
            if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathRooted(parent) || root.Length == 0 ||
                (Path.DirectorySeparatorChar == '\\' && (root.Length < 3 || !root.EndsWith("\\", StringComparison.Ordinal)))) throw new ArgumentException("backupPath must be an absolute directory.");
            var directory = Path.Combine(Path.GetFullPath(parent), "block-edit-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); return directory;
        }
        private static PlcBackupEntry PlcBackup(PlcTarget row, string root, int index)
        {
            var entry = new PlcBackupEntry { blockPath = row.Path };
            try
            {
                if (!Convert.ToBoolean(Property(row.Value, "IsConsistent"))) throw new NotSupportedException("Block is inconsistent; V19 has no SIMATIC SD document backup fallback.");
                var path = Path.Combine(root, index.ToString("D6"), "block.xml");
                PlcExportXml(row.Value, path, true); entry.files.Add(PlcInspect(path)); entry.format = "xml"; entry.Succeeded = true;
            }
            catch (Exception ex) { entry.error = ex.GetType().Name + ": " + ex.Message; }
            return entry;
        }
        private static void PlcVerify(PlcBackupEntry entry)
        {
            try
            {
                if (!entry.Succeeded || entry.files.Count == 0) throw new IOException("No verified backup files for " + entry.blockPath);
                foreach (var file in entry.files) { var current = PlcInspect(file.path); if (current.bytes != file.bytes || current.sha256 != file.sha256) throw new IOException("Backup file changed: " + file.path); PlcReadXml(file.path); }
            }
            catch (Exception ex) { entry.Succeeded = false; entry.error = ex.Message; foreach (var file in entry.files) file.readable = false; throw; }
        }
        private static void PlcManifest(PlcEditResult result)
            => File.WriteAllText(Path.Combine(result.backupPath, "manifest.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        private static void PlcFailure(PlcEditResult result, Exception ex)
        {
            result.Succeeded = false; result.Meta["success"] = false;
            result.Message = ex.GetType().Name + ": " + ex.Message + (result.mutationAttempted ? " Modification was attempted; inspect current state and backup. No automatic rollback." : " No block modification was attempted.");
            result.blockers.Add(ex.GetType().Name + ": " + ex.Message);
        }
        private static void PlcEditable(PlcTarget row)
        {
            var language = Convert.ToString(Property(row.Value, "ProgrammingLanguage")) ?? "";
            var secondary = Convert.ToString(PlcOptional(row.Value, "SecondaryType")) ?? "";
            if (row.Restricted || language.StartsWith("F_", StringComparison.Ordinal) || language == "FCP" || language == "FLD" || language == "CPU_DB" || language == "SDB" || secondary.IndexOf("Safety", StringComparison.OrdinalIgnoreCase) >= 0) throw new InvalidOperationException("Safety, system or technology block: " + row.Path);
            if (Convert.ToBoolean(Property(row.Value, "IsKnowHowProtected"))) throw new InvalidOperationException("Know-how protected block: " + row.Path);
            var protection = Service(row.Value, "SW.Blocks.PlcBlockWriteProtectionProvider");
            if (protection != null && (Convert.ToBoolean(Property(protection, "IsProtected")) || Convert.ToBoolean(Property(protection, "IsDefined")))) throw new InvalidOperationException("Block write protection is active or defined: " + row.Path);
        }
        private static void PlcRenameCheck(List<PlcTarget> rows, PlcTarget target, string? newName)
        {
            if (newName == null) return;
            if (string.Equals(Name(target.Value), newName, StringComparison.OrdinalIgnoreCase) || rows.Any(r => string.Equals(Name(r.Value), newName, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("New name conflicts with an existing software-wide block name.");
        }
        private static PlcEditResult PlcEdit(string softwarePath, string blockPath, string? newName, string backupPath)
        {
            lock (Gate)
            {
                var result = new PlcEditResult { softwarePath = softwarePath, blockPath = blockPath };
                try
                {
                    PlcLiteral(blockPath, "blockPath");
                    if (newName != null) { PlcLiteral(newName, "newName"); if (newName.Contains("/")) throw new ArgumentException("newName must be a single literal name."); }
                    var software = PlcSoftware(softwarePath); var rows = PlcEnumerate(software, true); var target = PlcResolve(rows, software, blockPath);
                    PlcEditable(target); PlcRenameCheck(rows, target, newName);
                    result.blockPath = target.Path; result.totalBlocks = 1; result.backupPath = PlcBackupDirectory(backupPath);
                    var backup = PlcBackup(target, result.backupPath, 0); result.backups.Add(backup); result.complete = backup.Succeeded; result.readable = backup.Succeeded;
                    PlcManifest(result); PlcVerify(backup);
                    var currentSoftware = PlcSoftware(softwarePath);
                    if (!currentSoftware.Equals(software)) throw new InvalidOperationException("Software identity changed during backup.");
                    var currentRows = PlcEnumerate(currentSoftware, true); var current = PlcResolve(currentRows, software, target.Path);
                    if (!current.Value.Equals(target.Value) || !Equals(current.Modified, target.Modified)) throw new InvalidOperationException("Target identity or modification timestamp changed during backup.");
                    PlcEditable(current); PlcRenameCheck(currentRows, current, newName); PlcVerify(backup);
                    result.mutationAttempted = true;
                    if (newName == null) Invoke(current.Value, "Delete");
                    else (current.Value.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException("Name")).SetValue(current.Value, newName);
                    var after = PlcEnumerate(software, true);
                    if (after.Any(r => r.Path == target.Path)) throw new InvalidOperationException("Original block path still exists after mutation.");
                    if (newName != null)
                    {
                        var slash = target.Path.LastIndexOf('/'); result.newBlockPath = (slash < 0 ? "" : target.Path.Substring(0, slash + 1)) + newName;
                        if (after.Count(r => r.Path == result.newBlockPath && r.Value.Equals(current.Value)) != 1) throw new InvalidOperationException("Renamed block identity/path postcondition failed.");
                    }
                    result.postconditionVerified = true; result.Succeeded = true; result.Meta["success"] = true; result.Message = "Block " + (newName == null ? "deleted" : "renamed") + "; target backup verified. Validate references, compile and save separately.";
                }
                catch (Exception ex) { result.complete = result.backups.Count > 0 && result.backups.All(b => b.Succeeded); result.readable = result.complete; PlcFailure(result, ex); }
                if (result.backupPath != "") { try { PlcManifest(result); } catch (Exception ex) { PlcFailure(result, ex); } }
                return result;
            }
        }
        [McpServerTool(Name = "DeleteBlock"), Description("Delete one exact PLC block only after a verified XML backup. Rejects safety/system/technology/protected blocks. Does not compile or save.")]
        public static PlcEditResult DeleteBlock(string softwarePath, string blockPath, string backupPath) => PlcEdit(softwarePath, blockPath, null, backupPath);
        [McpServerTool(Name = "RenameBlock"), Description("Rename an exact PLC block after verified backup and software-wide name conflict checks; rejects safety/system/protected blocks. Does not compile or save.")]
        public static PlcEditResult RenameBlock(string softwarePath, string blockPath, string newName, string backupPath) => PlcEdit(softwarePath, blockPath, newName ?? "", backupPath);
        [McpServerTool(Name = "BackupSoftwareForEdit"), Description("Back up all user, system, unit and technology blocks to verified nonempty XML files. complete requires stable enumeration and every backup verified; inconsistent V19 blocks have no document fallback. Not a project archive.")]
        public static PlcEditResult BackupSoftwareForEdit(string softwarePath, string backupPath)
        {
            lock (Gate)
            {
                var result = new PlcEditResult { softwarePath = softwarePath, backupScope = "software-blocks" };
                try
                {
                    var software = PlcSoftware(softwarePath); var rows = PlcEnumerate(software, true); result.totalBlocks = rows.Count; result.backupPath = PlcBackupDirectory(backupPath);
                    for (var i = 0; i < rows.Count; i++) { var backup = PlcBackup(rows[i], result.backupPath, i); result.backups.Add(backup); if (!backup.Succeeded) result.blockers.Add(backup.blockPath + ": " + backup.error); }
                    var currentSoftware = PlcSoftware(softwarePath); var after = PlcEnumerate(currentSoftware, true);
                    if (!software.Equals(currentSoftware) || rows.Count != after.Count || rows.Any(r => !after.Any(a => a.Path == r.Path && a.Value.Equals(r.Value) && Equals(a.Modified, r.Modified)))) result.blockers.Add("Block inventory or modification timestamps changed during backup.");
                    foreach (var backup in result.backups.Where(b => b.Succeeded)) PlcVerify(backup);
                    result.complete = result.blockers.Count == 0 && result.backups.All(b => b.Succeeded); result.readable = result.complete;
                    result.Succeeded = result.complete; result.Meta["success"] = result.complete;
                    result.Message = result.complete ? "Every enumerated block has verified XML backup files. Tags, PLC types, hardware and other project data are not included." : "Backup incomplete; inspect blockers.";
                    PlcManifest(result);
                }
                catch (Exception ex) { result.complete = false; result.readable = false; PlcFailure(result, ex); }
                if (result.backupPath != "") { try { PlcManifest(result); } catch (Exception ex) { result.complete = false; result.readable = false; PlcFailure(result, ex); } }
                return result;
            }
        }
        private static object PlcDocumentUnsupported(string tool) => new { Succeeded = false, Supported = false, capability = "SIMATIC_SD", tiaVersion = 19, requiredVersion = 20, Message = tool + " requires TIA Portal V20 or newer SIMATIC SD APIs. V19 supports SIMATIC ML XML and standard SCL external sources; formats are not interchangeable.", Meta = new { success = false, supported = false } };
        [McpServerTool(Name = "ExportAsDocuments"), Description("SIMATIC SD export is not supported by V19; returns explicit capability failure without modifying files.")]
        public static object ExportAsDocuments(string softwarePath, string blockPath, string exportPath, bool preservePath = false) => PlcDocumentUnsupported("ExportAsDocuments");
        [McpServerTool(Name = "ExportBlocksAsDocuments"), Description("SIMATIC SD batch export requires V20+; returns explicit unsupported capability.")]
        public static object ExportBlocksAsDocuments(string softwarePath, string exportPath, string regexName = "", bool preservePath = false) => PlcDocumentUnsupported("ExportBlocksAsDocuments");
        [McpServerTool(Name = "ImportFromDocuments"), Description("SIMATIC SD import requires V20+; returns explicit unsupported capability. Use ImportExternalSource for standard SCL, not renamed documents.")]
        public static object ImportFromDocuments(string softwarePath, string groupPath, string importPath, string fileNameWithoutExtension, string importOption = "Override") => PlcDocumentUnsupported("ImportFromDocuments");
        [McpServerTool(Name = "ImportBlocksFromDocuments"), Description("SIMATIC SD batch import requires V20+; returns explicit unsupported capability.")]
        public static object ImportBlocksFromDocuments(string softwarePath, string groupPath, string importPath, string regexName = "", string importOption = "Override") => PlcDocumentUnsupported("ImportBlocksFromDocuments");
        [McpServerTool(Name = "ImportExternalSource"), Description("Import a standard .scl external source using V19 CreateFromFile and GenerateBlocksFromSource. Generates/overwrites PLC blocks, retains source, does not compile or save. Back up software before use; not a SIMATIC SD converter.")]
        public static object ImportExternalSource(string softwarePath, string importPath)
        {
            lock (Gate)
            {
                var file = new FileInfo(importPath);
                if (!file.Extension.Equals(".scl", StringComparison.OrdinalIgnoreCase) || !file.Exists || file.Length == 0) throw new ArgumentException("Use an existing nonempty standard .scl external source file.");
                var text = File.ReadAllText(file.FullName).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                if (text.StartsWith("<", StringComparison.Ordinal) || text.StartsWith("{", StringComparison.Ordinal)) throw new ArgumentException("Expected standard SCL text, not XML/JSON or renamed SIMATIC SD.");
                var sources = Property(Property(PlcSoftware(softwarePath), "ExternalSourceGroup")!, "ExternalSources")!;
                if (Items(sources).Any(s => string.Equals(Name(s), file.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(Name(s), Path.GetFileNameWithoutExtension(file.Name), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("An external source with this name already exists; choose a distinct source filename.");
                // V19 System Manual 5.11.3.17: CreateFromFile(string name, string path).
                var source = Invoke(sources, "CreateFromFile", file.Name, file.FullName) ?? throw new InvalidOperationException("CreateFromFile returned no external source.");
                try
                {
                    var generated = Items(Invoke(source, "GenerateBlocksFromSource")).ToArray();
                    if (generated.Length == 0) throw new InvalidOperationException("GenerateBlocksFromSource returned no blocks.");
                    return new { Succeeded = true, Message = "External source imported and blocks generated; inspect diagnostics, compile and save separately.", SourceName = Name(source), Items = generated.Select(b => new { Name = Name(b), TypeName = b.GetType().Name }).ToArray(), mutationAttempted = true, Meta = new { success = true } };
                }
                catch (Exception ex) { throw new InvalidOperationException("Source '" + Name(source) + "' was created; generation may have partially modified blocks. Inspect current software before retrying. " + ex.Message, ex); }
            }
        }
    }
}
