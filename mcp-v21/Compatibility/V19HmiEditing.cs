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

namespace TiaMcpServer.Compatibility
{
    public sealed class V19HmiEditResult
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

    // V19 public reflection adapter. Preserves V21 Classic-only editing and fail-closed verification.
    public static partial class V19Tools
    {
        private sealed class HmiTargetRecord
        {
            public object Object = null!;
            public string Path = "", Name = "", XmlType = "";
            public Action<FileInfo> Export = null!;
            public Action<FileInfo>? Import;
        }
        private static HmiTargetRecord HmiResolve(string softwarePath, string objectPath)
        {
            HmiValidatePath(softwarePath); HmiValidatePath(objectPath);
            var software = HmiSoftware(softwarePath);
            if (!HmiClassic(software)) throw new NotSupportedException("Editing supports Classic HmiTarget only; Unified editing remains excluded.");
            var rows = HmiRows(software, "all").Where(r => r.Path == objectPath &&
                (r.Path.StartsWith("screens/", StringComparison.Ordinal) || r.Path.StartsWith("templates/", StringComparison.Ordinal) || r.Path == "special/globalElements" || r.Kind == "tag" || r.Kind == "connection") && r.Kind != "folder" && r.Kind != "table").ToArray();
            if (rows.Length != 1) throw new ArgumentException("Exact canonical objectPath must resolve to one supported Classic object.");
            var row = rows[0];
            return new HmiTargetRecord { Object = row.Object, Path = row.Path, Name = row.Path == "special/globalElements" ? "" : row.Name,
                XmlType = row.Path == "special/globalElements" ? "Hmi.Screen.ScreenGlobalElements" : row.Path.StartsWith("templates/", StringComparison.Ordinal) ? "Hmi.Screen.ScreenTemplate" : "Hmi.Screen.Screen",
                Export = f => HmiExport(row.Object, f, "WithDefaults"),
                Import = row.Kind != "screen" || (row.Path == "special/globalElements" && software.GetType().GetMethod("ImportScreenGlobalElements", new[] { typeof(FileInfo), Type("ImportOptions") }) == null) ? null : (Action<FileInfo>)(f => {
                    if (row.Path == "special/globalElements") Invoke(software, "ImportScreenGlobalElements", f, Enum.Parse(Type("ImportOptions"), "Override"));
                    else Invoke(row.Composition!, "Import", f, Enum.Parse(Type("ImportOptions"), "Override"));
                }) };
        }
        private static V19HmiEditResult HmiRun(string softwarePath, string objectPath, Action<V19HmiEditResult> action)
        {
            var result = new V19HmiEditResult { softwarePath = softwarePath, objectPath = objectPath };
            lock (Gate)
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
        private static bool HmiScalar(Type type) => type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) ||
            type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type.IsEnum;
        private static JsonNode? HmiValue(object? value)
        {
            if (value == null) return null;
            if (!HmiScalar(value.GetType())) throw new NotSupportedException("Unsupported attribute value type: " + value.GetType().FullName);
            return value.GetType().IsEnum ? JsonValue.Create(value.ToString()) : JsonSerializer.SerializeToNode(value, value.GetType());
        }
        public static object HmiConvertValue(JsonElement value, Type type)
        {
            if (!HmiScalar(type) || value.ValueKind == JsonValueKind.Null) throw new NotSupportedException("Only non-null scalar values with a known current SDK type are writable.");
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
        private static void HmiInspect(HmiTargetRecord target, V19HmiEditResult result)
        {
            foreach (var info in HmiInfos(target.Object))
            {
                var row = new JsonObject { ["accessMode"] = info.AccessMode.ToString(), ["writable"] = false,
                    ["supportedTypes"] = JsonSerializer.SerializeToNode(info.SupportedTypes.Select(t => t.FullName).ToArray()) };
                result.attributes[info.Name] = row;
                if (!info.Read) { row["diagnostic"] = "Write-only attribute is excluded: no compare/readback possible."; continue; }
                try
                {
                    var current = HmiAttribute(target.Object, info.Name);
                    row["type"] = current?.GetType().FullName;
                    row["value"] = HmiValue(current);
                    row["writable"] = current != null && HmiScalar(current.GetType()) && info.Write && info.Name != "Name";
                    if (current != null && current.GetType().IsEnum) row["enumNames"] = JsonSerializer.SerializeToNode(Enum.GetNames(current.GetType()));
                }
                catch (Exception ex) { row["diagnostic"] = ex.GetType().FullName + ": " + ex.Message; }
            }
            result.diagnostics.Add(target.Import == null ? "XML export and scalar attribute writes only; XML import is unavailable for this object category or installed V19 member set." : "XML replacement supported for this existing object; no rename, save or compile. XML verification is conservative and may report an unverified mutation after SDK normalization.");
        }
        [ModelContextProtocol.Server.McpServerTool(Name = "GetHmiEditableObject"), System.ComponentModel.Description("Inspect exact existing Classic HMI screen/template/global-elements/tag/connection SDK attributes and writable scalar types. Use canonical objectPath from HMI reading tools, including category and folders. No aliases, Unified editing, create, delete or rename. No project mutation.")]
        public static V19HmiEditResult GetHmiEditableObject(string softwarePath, string objectPath) => HmiRun(softwarePath, objectPath, result =>
        {
            HmiInspect(HmiResolve(softwarePath, objectPath), result);
            result.postconditionVerified = true;
        });
        private static void HmiExpect(HmiTargetRecord target, Dictionary<string, JsonElement> expected)
        {
            if (expected == null || expected.Count == 0 || expected.Count > 128) throw new ArgumentException("expectedValues must contain 1..128 exact readable scalar attributes.");
            var infos = HmiInfos(target.Object).ToDictionary(i => i.Name, StringComparer.Ordinal);
            foreach (var item in expected)
            {
                if (!infos.TryGetValue(item.Key, out var info) || !info.Read) throw new ArgumentException("Expected attribute is not readable: " + item.Key);
                var actual = HmiAttribute(target.Object, item.Key) ?? throw new NotSupportedException("Null expected attribute type is unknown: " + item.Key);
                if (!Equals(actual, HmiConvertValue(item.Value, actual.GetType()))) throw new InvalidOperationException("Expected value mismatch: " + item.Key + ". Refresh GetHmiEditableObject.");
            }
        }
        public static string HmiLocalFile(string path, bool mustExist)
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
        public static string HmiHash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        private static byte[] HmiReadBytes(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("XML artifact must be 1 byte..64 MiB.");
                using (var output = new MemoryStream()) { stream.CopyTo(output); return output.ToArray(); }
            }
        }
        private static byte[] HmiBackup(HmiTargetRecord target, string path, V19HmiEditResult result)
        {
            path = HmiLocalFile(path, false);
            // Siemens Export requires a nonexistent output. The caller must control this local directory.
            target.Export(new FileInfo(path));
            result.backupPath = path;
            var bytes = HmiReadBytes(path);
            HmiParse(bytes);
            result.sha256 = HmiHash(bytes);
            return bytes;
        }
        private static XDocument HmiParse(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 }))
                return XDocument.Load(reader);
        }
        public static XElement HmiValidateImport(byte[] bytes, string xmlType, string name)
        {
            var doc = HmiParse(bytes);
            if (doc.Root?.Name != XName.Get("Document")) throw new InvalidDataException("Expected unnamespaced Simatic ML Document root.");
            var objects = doc.Root.Elements().Where(e => e.Name.LocalName != "Engineering" && e.Name.LocalName != "DocumentInfo").ToList();
            if (objects.Count != 1 || objects[0].Name != XName.Get(xmlType)) throw new InvalidDataException("Import must contain exactly one matching top-level object; additional objects are forbidden.");
            var obj = objects[0];
            if (obj.Elements("AttributeList").Count() != 1 || obj.Element("AttributeList")!.Elements("Name").Count() > 1)
                throw new InvalidDataException("Expected exactly one AttributeList and at most one identity Name; duplicate identities are forbidden.");
            if (name.Length != 0 && (string?)obj.Element("AttributeList")?.Element("Name") != name) throw new InvalidDataException("Import cannot rename or create an object; Name must match the existing target exactly.");
            return obj;
        }
        [ModelContextProtocol.Server.McpServerTool(Name = "ExportHmiObject"), System.ComponentModel.Description("Export one existing Classic screen/template/global-elements/tag/connection with public V19 Export(WithDefaults), excluding read-only values for editing. Writes a new local XML artifact, does not mutate the project. Returns exact-byte sha256. Rejects existing output files.")]
        public static V19HmiEditResult ExportHmiObject(string softwarePath, string objectPath, string backupPath) => HmiRun(softwarePath, objectPath, result =>
        {
            HmiBackup(HmiResolve(softwarePath, objectPath), backupPath, result);
            result.postconditionVerified = true;
            result.diagnostics.Add("XML exported; no project mutation. sha256 hashes exact artifact bytes.");
        });
        private static void HmiRequireCompiler(string softwarePath)
        {
            if (Service(HmiSoftware(softwarePath), "Compiler.ICompilable") == null)
                throw new NotSupportedException("This HMI has no public compilation service; modification was not attempted.");
        }
        [ModelContextProtocol.Server.McpServerTool(Name = "SetHmiObjectAttributes"), System.ComponentModel.Description("WRITE existing Classic HMI object via public V19 SetAttributes, only SDK-advertised readable/writable scalar attributes. Requires expectedValues for every changed field, exports backup first, then verifies typed SDK readback. Partial failures are possible; inspect mutationAttempted and postconditionVerified. No rename, save, compile or automatic rollback.")]
        public static V19HmiEditResult SetHmiObjectAttributes(string softwarePath, string objectPath, Dictionary<string, JsonElement> values,
            Dictionary<string, JsonElement> expectedValues, string backupPath) => HmiRun(softwarePath, objectPath, result =>
        {
            HmiRequireCompiler(softwarePath);
            var target = HmiResolve(softwarePath, objectPath);
            if (values == null || values.Count == 0 || values.Count > 128) throw new ArgumentException("values must contain 1..128 attributes.");
            HmiExpect(target, expectedValues);
            var infos = HmiInfos(target.Object).ToDictionary(i => i.Name, StringComparer.Ordinal);
            var changes = new List<KeyValuePair<string, object>>();
            foreach (var item in values)
            {
                if (item.Key == "Name" || !infos.TryGetValue(item.Key, out var info) || !info.Write || !info.Read)
                    throw new ArgumentException("Attribute is not an allowed readable/writable attribute: " + item.Key);
                if (!expectedValues.ContainsKey(item.Key)) throw new ArgumentException("Every changed attribute requires an expected value: " + item.Key);
                var current = HmiAttribute(target.Object, item.Key) ?? throw new NotSupportedException("Cannot infer type of a null attribute.");
                changes.Add(new KeyValuePair<string, object>(item.Key, HmiConvertValue(item.Value, current.GetType())));
            }
            HmiBackup(target, backupPath, result);
            HmiExpect(target, expectedValues);
            result.mutationAttempted = true;
            HmiEngineering(target.Object, "SetAttributes", changes);
            foreach (var change in changes)
                if (!Equals(HmiAttribute(target.Object, change.Key), change.Value)) throw new InvalidOperationException("Attribute readback mismatch: " + change.Key);
            result.postconditionVerified = true;
            HmiInspect(target, result);
            result.diagnostics.Add("Changed attributes verified by SDK readback. No automatic rollback; backup retained. Project not saved or compiled.");
        });
        [ModelContextProtocol.Server.McpServerTool(Name = "ImportHmiObject"), System.ComponentModel.Description("WRITE one existing Classic screen/template/global-elements object using public V19 composition Import(Override) or HmiTarget.ImportScreenGlobalElements. Input must be one matching Simatic ML object with unchanged name. Requires edited input SHA256 and expected current attributes; backs up before import and conservatively checks re-export. Can change/delete child screen content as specified by XML. No top-level create/delete/rename, Unified, tag/connection import, save, compile or automatic rollback. A returned SDK import is not success unless postconditionVerified is true.")]
        public static V19HmiEditResult ImportHmiObject(string softwarePath, string objectPath, string importPath, string expectedSha256,
            Dictionary<string, JsonElement> expectedValues, string backupPath) => HmiRun(softwarePath, objectPath, result =>
        {
            HmiRequireCompiler(softwarePath);
            var target = HmiResolve(softwarePath, objectPath);
            if (target.Import == null) throw new NotSupportedException("Import supports existing Classic screen, template and global-elements objects only.");
            importPath = HmiLocalFile(importPath, true);
            // Lock the source against replacement/writes for the entire public SDK import.
            using (var source = new FileStream(importPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length == 0 || source.Length > 64 * 1024 * 1024) throw new InvalidDataException("XML artifact must be 1 byte..64 MiB.");
                byte[] bytes;
                using (var memory = new MemoryStream()) { source.CopyTo(memory); bytes = memory.ToArray(); }
                if (expectedSha256 == null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit) || !string.Equals(HmiHash(bytes), expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Import SHA256 mismatch; expectedSha256 must hash the edited input file bytes.");
                var expectedXml = HmiValidateImport(bytes, target.XmlType, target.Name);
                string verification = HmiLocalFile(HmiLocalFile(backupPath, false) + ".verified.xml", false);
                HmiExpect(target, expectedValues);
                var original = HmiBackup(target, backupPath, result);
                // Check the actual SDK export shape as well; unknown serializers fail closed.
                var originalXml = HmiValidateImport(original, target.XmlType, target.Name);
                if ((string?)originalXml.Element("AttributeList")?.Element("Name") != (string?)expectedXml.Element("AttributeList")?.Element("Name"))
                    throw new InvalidDataException("Import cannot change the exported object name.");
                HmiExpect(target, expectedValues);
                result.mutationAttempted = true;
                target.Import(new FileInfo(importPath));
                var readback = HmiResolve(softwarePath, objectPath);
                HmiLocalFile(verification, false);
                readback.Export(new FileInfo(verification));
                var actualXml = HmiValidateImport(HmiReadBytes(verification), target.XmlType, target.Name);
                result.postconditionVerified = XNode.DeepEquals(expectedXml, actualXml);
                result.diagnostics.Add("Readback artifact: " + verification);
                result.diagnostics.Add(result.postconditionVerified ? "Imported object XML verified by re-export." : "SDK import returned, but re-export differs from input. Mutation may have occurred; inspect both XML files. No rollback, save or compile performed.");
            }
        });
    }
}