using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.Hmi.Communication;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    public sealed class HmiEditResult
    {
        public bool Succeeded { get; set; }
        public JsonObject meta { get; set; } = new JsonObject { ["success"] = false };
        public bool mutationAttempted { get; set; }
        public bool postconditionVerified { get; set; }
        public string backupPath { get; set; } = "";
        public string softwarePath { get; set; } = "";
        public string objectPath { get; set; } = "";
        public string sha256 { get; set; } = "";
        public JsonObject attributes { get; set; } = new JsonObject();
        public List<string> diagnostics { get; set; } = new List<string>();
    }

    // Deliberately bounded to Classic objects. No service discovery, reflection invocation,
    // inferred creates/deletes, project save, compile, attach, or online operations.
    public static class HmiEditing
    {
        private static readonly object EditLock = new object();
        private sealed class Target
        {
            public IEngineeringObject Object = null!;
            public string Path = "";
            public string Name = "";
            public string XmlType = "";
            public Action<FileInfo> Export = null!;
            public Action<FileInfo>? Import;
        }

        private static string Join(string parent, string name)
        {
            HmiReading.ValidatePath(name);
            if (name.Contains("/")) throw new InvalidOperationException("An HMI name contains a path separator.");
            return parent + "/" + name;
        }
        private static void Depth(int depth)
        {
            if (depth > 64) throw new InvalidOperationException("HMI hierarchy exceeds depth 64.");
        }
        private static IEnumerable<Target> Screens(ScreenFolder folder, string parent, int depth)
        {
            Depth(depth);
            string path = Join(parent, folder.Name);
            foreach (var screen in folder.Screens)
                yield return new Target { Object = screen, Path = Join(path, screen.Name), Name = screen.Name, XmlType = "Hmi.Screen.Screen",
                    Export = f => screen.Export(f, ExportOptions.WithDefaults), Import = f => folder.Screens.Import(f, ImportOptions.Override) };
            foreach (var child in folder.Folders)
                foreach (var target in Screens(child, path, depth + 1)) yield return target;
        }
        private static IEnumerable<Target> Templates(ScreenTemplateFolder folder, string parent, int depth)
        {
            Depth(depth);
            string path = Join(parent, folder.Name);
            foreach (var screen in folder.ScreenTemplates)
                yield return new Target { Object = screen, Path = Join(path, screen.Name), Name = screen.Name, XmlType = "Hmi.Screen.ScreenTemplate",
                    Export = f => screen.Export(f, ExportOptions.WithDefaults), Import = f => folder.ScreenTemplates.Import(f, ImportOptions.Override) };
            foreach (var child in folder.Folders)
                foreach (var target in Templates(child, path, depth + 1)) yield return target;
        }
        private static IEnumerable<Target> Tags(TagFolder folder, string parent, int depth)
        {
            Depth(depth);
            string path = Join(parent, folder.Name);
            foreach (var table in folder.TagTables)
                foreach (var tag in table.Tags)
                    yield return new Target { Object = tag, Name = tag.Name, Path = Join(Join(path, table.Name) + "/tags", tag.Name),
                        Export = f => tag.Export(f, ExportOptions.WithDefaults) };
            foreach (var child in folder.Folders)
                foreach (var target in Tags(child, path, depth + 1)) yield return target;
        }
        private static IEnumerable<Target> Inventory(HmiTarget software)
        {
            foreach (var target in Screens(software.ScreenFolder, "screens", 0)) yield return target;
            foreach (var target in Templates(software.ScreenTemplateFolder, "templates", 0)) yield return target;
            if (software.ScreenGlobalElements != null)
            {
                var global = software.ScreenGlobalElements;
                yield return new Target { Object = global, Path = "special/globalElements", XmlType = "Hmi.Screen.ScreenGlobalElements",
                    Export = f => global.Export(f, ExportOptions.WithDefaults), Import = f => software.ImportScreenGlobalElements(f, ImportOptions.Override) };
            }
            foreach (var target in Tags(software.TagFolder, "tables", 0)) yield return target;
            foreach (var connection in software.Connections)
                yield return new Target { Object = connection, Name = connection.Name, Path = Join("connections", connection.Name),
                    Export = f => connection.Export(f, ExportOptions.WithDefaults) };
        }
        private static Target Resolve(Portal portal, string softwarePath, string objectPath)
        {
            HmiReading.ValidatePath(softwarePath); HmiReading.ValidatePath(objectPath);
            if (!portal.IsConnected() || !portal.ProjectIsValid) throw new InvalidOperationException("An existing selected project is required.");
            // Verify canonical software path against the same public hardware inventory as HmiReading.
            var state = portal.GetState();
            var projects = portal.IsLocalSession ? portal.GetSessions() : portal.GetProjects();
            var selected = projects.Where(p => p.Name == state.Project).ToList();
            if (selected.Count != 1) throw new InvalidOperationException("Cannot uniquely verify the active project.");
            var entries = EngineeringPaths.Inventory(selected[0]).Where(p => p.Kind == "software" && p.Path == softwarePath).ToList();
            if (entries.Count != 1) throw new InvalidOperationException("Exact canonical softwarePath must resolve uniquely; use GetHmiSoftware.");
            var software = ((global::Siemens.Engineering.HW.Features.SoftwareContainer)entries[0].Target).Software as HmiTarget
                ?? throw new NotSupportedException("Editing supports Classic HmiTarget only; Unified is not supported.");
            var targets = Inventory(software).Take(100001).ToList();
            if (targets.Count > 100000) throw new InvalidOperationException("HMI inventory exceeds 100000 objects.");
            var matches = targets.Where(t => t.Path == objectPath).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Exact objectPath must identify one existing supported Classic screen, template, global-elements, tag or connection. No aliases or creates are accepted.");
            return matches[0];
        }
        private static HmiEditResult Run(string softwarePath, string objectPath, Action<HmiEditResult> action)
        {
            var result = new HmiEditResult { softwarePath = softwarePath, objectPath = objectPath };
            lock (EditLock)
            {
                try { action(result); result.Succeeded = result.postconditionVerified; }
                catch (Exception ex)
                {
                    // Preserve Siemens messages and inner exceptions verbatim.
                    for (var error = ex; error != null; error = error.InnerException)
                        result.diagnostics.Add(error.GetType().FullName + ": " + error.Message);
                }
                result.meta["success"] = result.Succeeded;
            }
            return result;
        }
        private static bool Scalar(Type type) => type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) ||
            type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type.IsEnum;
        private static JsonNode? Value(object? value)
        {
            if (value == null) return null;
            if (!Scalar(value.GetType())) throw new NotSupportedException("Unsupported attribute value type: " + value.GetType().FullName);
            return value.GetType().IsEnum ? JsonValue.Create(value.ToString()) : JsonSerializer.SerializeToNode(value, value.GetType());
        }
        public static object ConvertValue(JsonElement value, Type type)
        {
            if (!Scalar(type) || value.ValueKind == JsonValueKind.Null) throw new NotSupportedException("Only non-null scalar values with a known current SDK type are writable.");
            if (type.IsEnum)
            {
                if (value.ValueKind != JsonValueKind.String || !Enum.GetNames(type).Contains(value.GetString(), StringComparer.Ordinal))
                    throw new ArgumentException("Enum value must be one exact declared name.");
                return Enum.Parse(type, value.GetString()!, false);
            }
            var converted = JsonSerializer.Deserialize(value.GetRawText(), type)!;
            if (converted is double d && (double.IsNaN(d) || double.IsInfinity(d)) || converted is float f && (float.IsNaN(f) || float.IsInfinity(f)))
                throw new ArgumentException("Non-finite numbers are not supported.");
            return converted;
        }
        private static void Inspect(Target target, HmiEditResult result)
        {
            foreach (var info in target.Object.GetAttributeInfos())
            {
                var row = new JsonObject { ["accessMode"] = info.AccessMode.ToString(), ["writable"] = false,
                    ["supportedTypes"] = JsonSerializer.SerializeToNode(info.SupportedTypes.Select(t => t.FullName).ToArray()) };
                result.attributes[info.Name] = row;
                if ((info.AccessMode & EngineeringAttributeAccessMode.Read) == 0) { row["diagnostic"] = "Write-only attribute is excluded: no compare/readback possible."; continue; }
                try
                {
                    var current = target.Object.GetAttribute(info.Name);
                    row["type"] = current?.GetType().FullName;
                    row["value"] = Value(current);
                    row["writable"] = current != null && Scalar(current.GetType()) && (info.AccessMode & EngineeringAttributeAccessMode.Write) != 0 && info.Name != "Name";
                    if (current != null && current.GetType().IsEnum) row["enumNames"] = JsonSerializer.SerializeToNode(Enum.GetNames(current.GetType()));
                }
                catch (Exception ex) { row["diagnostic"] = ex.GetType().FullName + ": " + ex.Message; }
            }
            result.diagnostics.Add(target.Import == null ? "XML export and scalar attribute writes only; XML import unavailable for this tool's tag/connection scope." : "XML replacement supported for this existing object; no rename, save or compile. XML verification is conservative and may report an unverified mutation after SDK normalization.");
        }
        public static HmiEditResult Get(Portal portal, string softwarePath, string objectPath) => Run(softwarePath, objectPath, result =>
        {
            Inspect(Resolve(portal, softwarePath, objectPath), result);
            result.postconditionVerified = true;
        });
        private static void Expect(Target target, Dictionary<string, JsonElement> expected)
        {
            if (expected == null || expected.Count == 0 || expected.Count > 128) throw new ArgumentException("expectedValues must contain 1..128 exact readable scalar attributes.");
            var infos = target.Object.GetAttributeInfos().ToDictionary(i => i.Name, StringComparer.Ordinal);
            foreach (var item in expected)
            {
                if (!infos.TryGetValue(item.Key, out var info) || (info.AccessMode & EngineeringAttributeAccessMode.Read) == 0) throw new ArgumentException("Expected attribute is not readable: " + item.Key);
                var actual = target.Object.GetAttribute(item.Key) ?? throw new NotSupportedException("Null expected attribute type is unknown: " + item.Key);
                if (!Equals(actual, ConvertValue(item.Value, actual.GetType()))) throw new InvalidOperationException("Expected value mismatch: " + item.Key + ". Refresh GetHmiEditableObject.");
            }
        }
        public static string LocalFile(string path, bool mustExist)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 3 || path[1] != ':' || path.Substring(2).Contains(':'))
                throw new ArgumentException("Use an explicit absolute local drive file path; UNC, device paths and alternate data streams are rejected.");
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(full), ".xml", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Artifact path must end in .xml.");
            if (!Directory.Exists(Path.GetDirectoryName(full))) throw new DirectoryNotFoundException("Artifact parent directory must already exist.");
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir != null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Artifact ancestors cannot be reparse points.");
            if (Directory.Exists(full) || (File.Exists(full) && (!mustExist || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0))) throw new IOException("Artifact must be a regular file and output paths must not already exist.");
            if (mustExist && !File.Exists(full)) throw new FileNotFoundException("Import file does not exist.", full);
            return full;
        }
        public static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static byte[] ReadBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("XML artifact must be 1 byte..64 MiB.");
                using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
            }
        }
        private static byte[] Backup(Target target, string path, HmiEditResult result)
        {
            path = LocalFile(path, false);
            // Siemens Export requires a nonexistent output. The caller must control this local directory.
            target.Export(new FileInfo(path));
            result.backupPath = path;
            var bytes = ReadBytes(path);
            Parse(bytes);
            result.sha256 = Hash(bytes);
            return bytes;
        }
        private static XDocument Parse(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 }))
                return XDocument.Load(reader);
        }
        public static XElement ValidateImport(byte[] bytes, string xmlType, string name)
        {
            var doc = Parse(bytes);
            if (doc.Root?.Name != XName.Get("Document")) throw new InvalidDataException("Expected unnamespaced Simatic ML Document root.");
            var objects = doc.Root.Elements().Where(e => e.Name.LocalName != "Engineering" && e.Name.LocalName != "DocumentInfo").ToList();
            if (objects.Count != 1 || objects[0].Name != XName.Get(xmlType)) throw new InvalidDataException("Import must contain exactly one matching top-level object; additional objects are forbidden.");
            var obj = objects[0];
            if (obj.Elements("AttributeList").Count() != 1 || obj.Element("AttributeList")!.Elements("Name").Count() > 1)
                throw new InvalidDataException("Expected exactly one AttributeList and at most one identity Name; duplicate identities are forbidden.");
            if (name.Length != 0 && (string?)obj.Element("AttributeList")?.Element("Name") != name) throw new InvalidDataException("Import cannot rename or create an object; Name must match the existing target exactly.");
            return obj;
        }
        public static HmiEditResult Export(Portal portal, string softwarePath, string objectPath, string backupPath) => Run(softwarePath, objectPath, result =>
        {
            Backup(Resolve(portal, softwarePath, objectPath), backupPath, result);
            result.postconditionVerified = true;
            result.diagnostics.Add("XML exported; no project mutation. sha256 hashes exact artifact bytes.");
        });
        private static void RequireCompiler(Portal portal, string softwarePath)
        {
            var provider = portal.GetSoftware(softwarePath) as IEngineeringServiceProvider;
            if (provider?.GetService<global::Siemens.Engineering.Compiler.ICompilable>() == null)
                throw new NotSupportedException("This HMI has no public compilation service; modification was not attempted.");
        }
        public static HmiEditResult Set(Portal portal, string softwarePath, string objectPath, Dictionary<string, JsonElement> values,
            Dictionary<string, JsonElement> expectedValues, string backupPath) => Run(softwarePath, objectPath, result =>
        {
            RequireCompiler(portal, softwarePath);
            var target = Resolve(portal, softwarePath, objectPath);
            if (values == null || values.Count == 0 || values.Count > 128) throw new ArgumentException("values must contain 1..128 attributes.");
            Expect(target, expectedValues);
            var infos = target.Object.GetAttributeInfos().ToDictionary(i => i.Name, StringComparer.Ordinal);
            var changes = new List<KeyValuePair<string, object>>();
            foreach (var item in values)
            {
                if (item.Key == "Name" || !infos.TryGetValue(item.Key, out var info) || (info.AccessMode & EngineeringAttributeAccessMode.Write) == 0 || (info.AccessMode & EngineeringAttributeAccessMode.Read) == 0)
                    throw new ArgumentException("Attribute is not an allowed readable/writable attribute: " + item.Key);
                if (!expectedValues.ContainsKey(item.Key)) throw new ArgumentException("Every changed attribute requires an expected value: " + item.Key);
                var current = target.Object.GetAttribute(item.Key) ?? throw new NotSupportedException("Cannot infer type of a null attribute.");
                changes.Add(new KeyValuePair<string, object>(item.Key, ConvertValue(item.Value, current.GetType())));
            }
            Backup(target, backupPath, result);
            Expect(target, expectedValues);
            result.mutationAttempted = true;
            target.Object.SetAttributes(changes);
            foreach (var change in changes)
                if (!Equals(target.Object.GetAttribute(change.Key), change.Value)) throw new InvalidOperationException("Attribute readback mismatch: " + change.Key);
            result.postconditionVerified = true;
            Inspect(target, result);
            result.diagnostics.Add("Changed attributes verified by SDK readback. No automatic rollback; backup retained. Project not saved or compiled.");
        });
        public static HmiEditResult Import(Portal portal, string softwarePath, string objectPath, string importPath, string expectedSha256,
            Dictionary<string, JsonElement> expectedValues, string backupPath) => Run(softwarePath, objectPath, result =>
        {
            RequireCompiler(portal, softwarePath);
            var target = Resolve(portal, softwarePath, objectPath);
            if (target.Import == null) throw new NotSupportedException("Import supports existing Classic screen, template and global-elements objects only.");
            importPath = LocalFile(importPath, true);
            // Lock the source against replacement/writes for the entire public SDK import.
            using (var source = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length == 0 || source.Length > 64 * 1024 * 1024) throw new InvalidDataException("XML artifact must be 1 byte..64 MiB.");
                byte[] bytes;
                using (var memory = new MemoryStream()) { source.CopyTo(memory); bytes = memory.ToArray(); }
                if (expectedSha256 == null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit) || !string.Equals(Hash(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Import SHA256 mismatch; expectedSha256 must hash the edited input file bytes.");
                var expectedXml = ValidateImport(bytes, target.XmlType, target.Name);
                string verification = LocalFile(LocalFile(backupPath, false) + ".verified.xml", false);
                Expect(target, expectedValues);
                var original = Backup(target, backupPath, result);
                // Check the actual SDK export shape as well; unknown serializers fail closed.
                var originalXml = ValidateImport(original, target.XmlType, target.Name);
                if ((string?)originalXml.Element("AttributeList")?.Element("Name") != (string?)expectedXml.Element("AttributeList")?.Element("Name"))
                    throw new InvalidDataException("Import cannot change the exported object name.");
                Expect(target, expectedValues);
                result.mutationAttempted = true;
                target.Import(new FileInfo(importPath));
                var readback = Resolve(portal, softwarePath, objectPath);
                LocalFile(verification, false);
                readback.Export(new FileInfo(verification));
                var actualXml = ValidateImport(ReadBytes(verification), target.XmlType, target.Name);
                result.postconditionVerified = XNode.DeepEquals(expectedXml, actualXml);
                result.diagnostics.Add("Readback artifact: " + verification);
                result.diagnostics.Add(result.postconditionVerified ? "Imported object XML verified by re-export." : "SDK import returned, but re-export differs from input. Mutation may have occurred; inspect both XML files. No rollback, save or compile performed.");
            }
        });
    }
}
