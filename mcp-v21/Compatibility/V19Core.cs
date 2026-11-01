using ModelContextProtocol.Server;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaMcpServer.Compatibility
{
    public static partial class V19Tools
    {
        private static Type CoreSdkType(string name)
        {
            string fullName = "Siemens.Engineering." + name;
            var type = Sdk.GetType(fullName, false);
            if (type != null) return type;
            // Optional components must come from the same selected PublicAPI directory.
            // Never search another installed TIA version or load arbitrary plugin assemblies.
            foreach (string component in new[] { "Siemens.Engineering.Hmi", "Siemens.Engineering.HmiUnified", "Siemens.Engineering.Step7", "Siemens.Engineering.Safety" })
            {
                string file = Path.Combine(Path.GetDirectoryName(Sdk.Location)!, component + ".dll");
                if (!File.Exists(file)) continue;
                var identity = AssemblyName.GetAssemblyName(file);
                if (identity.Name != component || identity.Version.Major != 19)
                    throw new FileLoadException("Mismatched V19 installation component: " + file);
                type = Assembly.LoadFrom(file).GetType(fullName, false);
                if (type != null) return type;
            }
            throw new TypeLoadException("The selected V19 PublicAPI does not expose " + fullName + ". Check the installed component and target capability.");
        }

        // The V19 SDK explicitly implements some IEngineeringObject members.
        // Resolve only exact public signatures; do not traverse arbitrary object graphs.
        private static object? CoreProperty(object value, string name)
        {
            var type = value.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
                property = type.GetInterfaces().Where(i => (i.Namespace ?? "").StartsWith("Siemens.Engineering", StringComparison.Ordinal))
                    .Select(i => i.GetProperty(name)).FirstOrDefault(p => p != null);
            if (property == null) throw new MissingMemberException(type.FullName, name);
            try { return property.GetValue(value); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static object? CoreOptional(object value, string name)
        {
            try { return Property(value, name); }
            catch (MissingMemberException) { return null; }
        }
        private static object? CoreInvoke(object value, string name, object[] args)
        {
            var type = value as Type ?? value.GetType();
            var flags = BindingFlags.Public | (value is Type ? BindingFlags.Static : BindingFlags.Instance);
            Func<MethodInfo, bool> match = m => m.Name == name && !m.IsGenericMethod && m.GetParameters().Length == args.Length &&
                m.GetParameters().Select((p, i) => args[i] == null ? !p.ParameterType.IsValueType || Nullable.GetUnderlyingType(p.ParameterType) != null : p.ParameterType.IsInstanceOfType(args[i])).All(x => x);
            var methods = type.GetMethods(flags).Where(match).ToArray();
            if (methods.Length == 0 && !(value is Type))
                methods = type.GetInterfaces().Where(i => (i.Namespace ?? "").StartsWith("Siemens.Engineering", StringComparison.Ordinal))
                    .SelectMany(i => i.GetMethods()).Where(match).GroupBy(m => m.ToString()).Select(g => g.First()).ToArray();
            if (methods.Length != 1) throw new MissingMethodException(type.FullName, name + " (exact public signature)");
            try { return methods[0].Invoke(value is Type ? null : value, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private sealed class CoreSelection
        {
            public object Project = null!;
            public object? Session;
        }
        private static List<CoreSelection> CoreSelections()
        {
            if (portal == null) throw new InvalidOperationException("Connect to a TIA V19 instance first.");
            var result = Items(Property(portal, "Projects")).Select(p => new CoreSelection { Project = p }).ToList();
            var sessions = CoreOptional(portal, "LocalSessions");
            if (sessions != null)
                foreach (var session in Items(sessions))
                {
                    var project = Property(session, "Project") ?? throw new InvalidOperationException("Local session has no project.");
                    result.RemoveAll(r => ReferenceEquals(r.Project, project));
                    result.Add(new CoreSelection { Project = project, Session = session });
                }
            return result;
        }
        private static CoreSelection CoreSelected()
        {
            var choices = CoreSelections();
            if (choices.Count != 1) throw new InvalidOperationException("Exactly one open project or local session is required; found " + choices.Count + ".");
            return choices[0];
        }
        private static object CoreState()
        {
            var choices = portal == null ? new List<CoreSelection>() : CoreSelections();
            var chosen = choices.Count == 1 ? choices[0] : null;
            return new { IsConnected = portal != null, Project = chosen == null ? null : Name(chosen.Project),
                ProjectPath = chosen == null ? null : Convert.ToString(Property(chosen.Project, "Path")),
                Session = chosen?.Session == null ? null : Convert.ToString(CoreOptional(chosen.Session, "Name") ?? CoreOptional(chosen.Session, "Path")),
                ProjectCount = choices.Count, IsLocalSession = chosen?.Session != null };
        }
        private static object CoreProjects()
        {
            var selected = CoreSelections();
            return new { Message = "Open projects and sessions retrieved", Items = selected.Select(s => CoreInfo(s.Project, Convert.ToString(Property(s.Project, "Path")) ?? "")).ToArray(), Meta = new { success = true } };
        }
        [McpServerTool(Name = "OpenProject"), Description("Open an existing .ap19 or older project, or .als19 local session. An older local project is opened with the public upgrade API. Close any different open project explicitly first.")]
        public static object CoreOpenProject(string path)
        {
            lock (Gate)
            {
                var file = new FileInfo(path);
                if (!Path.IsPathRooted(path) || !file.Exists) throw new ArgumentException("Use an existing absolute project/session file path.");
                var match = Regex.Match(file.Extension, @"^\.(ap|als)(\d+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out int version) || version > 19 || version < 10)
                    throw new ArgumentException("TIA V19 opens .ap19 or older projects and .als19 sessions, not later-version projects.");
                bool session = match.Groups[1].Value.Equals("als", StringComparison.OrdinalIgnoreCase);
                if (session && version != 19) throw new ArgumentException("Local sessions must match V19.");
                var current = CoreSelections();
                if (current.Count != 0)
                {
                    if (current.Count == 1 && string.Equals(Convert.ToString(Property(current[0].Session ?? current[0].Project, "Path")), file.FullName, StringComparison.OrdinalIgnoreCase)) return Success("Project/session already open");
                    throw new InvalidOperationException("Close the current project/session explicitly before opening a different one.");
                }
                var collection = Property(portal!, session ? "LocalSessions" : "Projects")!;
                var opened = Invoke(collection, session || version == 19 ? "Open" : "OpenWithUpgrade", file);
                if (opened == null) throw new InvalidOperationException("SDK open returned no project/session.");
                CoreSelected();
                return Success("Project/session opened");
            }
        }
        [McpServerTool(Name = "SaveAsProject"), Description("Save the current local V19 project to a new directory. Multiuser local sessions do not support SaveAs.")]
        public static object CoreSaveAs(string newProjectPath)
        {
            lock (Gate)
            {
                var current = CoreSelected();
                if (current.Session != null) throw new NotSupportedException("SaveAs is not available for local sessions.");
                if (!Path.IsPathRooted(newProjectPath) || File.Exists(newProjectPath) || Directory.Exists(newProjectPath)) throw new ArgumentException("Use a new absolute project directory.");
                Invoke(current.Project, "SaveAs", new DirectoryInfo(newProjectPath));
                return Success("Project saved to " + Path.GetFullPath(newProjectPath));
            }
        }
        [McpServerTool(Name = "CloseProject"), Description("Explicitly close the current V19 project or local session. Does not save automatically.")]
        public static object CoreCloseProject()
        {
            lock (Gate)
            {
                var current = CoreSelected();
                Invoke(current.Session ?? current.Project, "Close");
                if (CoreSelections().Any(p => ReferenceEquals(p.Project, current.Project))) throw new InvalidOperationException("SDK close returned but project is still open.");
                return Success("Project/session closed");
            }
        }
        [McpServerTool(Name = "GetDeviceItemInfo"), Description("Read device-item metadata and all SDK-advertised attributes from its exact inventory path.")]
        public static object CoreDeviceItemInfo(string deviceItemPath)
        {
            lock (Gate) return CoreInfo(Find("deviceItem", deviceItemPath), deviceItemPath);
        }
        private static object? CoreValue(object? value, int depth = 0)
        {
            if (value == null || value is string || value is bool || value is decimal || value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong) return value;
            if (value is double d && !double.IsInfinity(d) && !double.IsNaN(d)) return d;
            if (value is float f && !float.IsInfinity(f) && !float.IsNaN(f)) return f;
            if (value is Enum || value is char || value is Guid || value is Version || value is Uri) return value.ToString();
            if (value is DateTime date) return date.ToString("O", CultureInfo.InvariantCulture);
            if (value is DateTimeOffset offset) return offset.ToString("O", CultureInfo.InvariantCulture);
            if (value is TimeSpan span) return span.ToString("c", CultureInfo.InvariantCulture);
            if (value is CultureInfo culture) return culture.Name;
            if (value is FileSystemInfo path) return path.FullName;
            if (depth < 8 && value is IList list && list.Count <= 256) return list.Cast<object?>().Select(v => CoreValue(v, depth + 1)).ToArray();
            throw new NotSupportedException("No bounded scalar projection for SDK value " + value.GetType().FullName);
        }
        private static List<Dictionary<string, object?>> CoreAttributes(object target)
        {
            var result = new List<Dictionary<string, object?>>();
            foreach (var attribute in Items(Invoke(target, "GetAttributeInfos")))
            {
                if (result.Count >= 4096) throw new InvalidOperationException("SDK attribute metadata exceeds 4096 fields.");
                string name = Name(attribute), access = Convert.ToString(Property(attribute, "AccessMode")) ?? "";
                var row = new Dictionary<string, object?> { ["Name"] = name, ["AccessMode"] = access, ["Status"] = "NotReadable", ["Succeeded"] = false };
                result.Add(row);
                if (access.IndexOf("Read", StringComparison.OrdinalIgnoreCase) < 0) { row["Error"] = "Attribute is not advertised as readable."; continue; }
                try
                {
                    object? value = Invoke(target, "GetAttribute", name);
                    row["ValueType"] = value?.GetType().FullName;
                    row["Status"] = "ProjectionFailed";
                    row["Value"] = CoreValue(value);
                    row["Status"] = "Success"; row["Succeeded"] = true;
                }
                catch (Exception ex)
                {
                    if (Equals(row["Status"], "NotReadable")) row["Status"] = "ReadFailed";
                    row["Error"] = ex.GetType().FullName + ": " + ex.Message;
                }
            }
            return result;
        }
        private static Dictionary<string, object?> CoreInfo(object target, string path)
        {
            var attributes = CoreAttributes(target);
            int failed = attributes.Count(a => !Equals(a["Succeeded"], true));
            return new Dictionary<string, object?> { ["Name"] = Name(target), ["TypeName"] = target.GetType().Name, ["Path"] = path,
                ["Attributes"] = attributes, ["Meta"] = new { success = failed == 0, partial = failed != 0, attributesComplete = failed == 0, attributeFailureCount = failed } };
        }
        private static Dictionary<string, object?> CoreBlockInfo(object block, string path)
        {
            var info = CoreInfo(block, path);
            info["path"] = path;
            foreach (string name in new[] { "Namespace", "ProgrammingLanguage", "MemoryLayout", "IsConsistent", "HeaderName", "ModifiedDate", "IsKnowHowProtected" })
                info[name] = CoreValue(CoreOptional(block, name));
            return info;
        }
        private static object CoreGetBlocks(string softwarePath, string regexName)
        {
            var regex = new Regex(regexName ?? "", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var rows = Blocks(softwarePath).Where(r => regex.IsMatch(r.name)).ToArray();
            return new { Items = rows.Select(r => CoreBlockInfo(r.Target, r.path)).ToArray(), Paths = rows, scope = "user-blocks", Meta = new { success = true } };
        }
        [McpServerTool(Name = "GetBlocksWithHierarchy"), Description("Read user block groups with block metadata and canonical paths.")]
        public static object CoreHierarchy(string softwarePath)
        {
            lock (Gate) return new { Root = CoreGroupHierarchy(Property(Find("software", softwarePath), "BlockGroup")!, "", 0), Meta = new { success = true } };
        }
        private static object CoreGroupHierarchy(object group, string prefix, int depth)
        {
            if (depth > 64) throw new InvalidOperationException("Group hierarchy exceeds 64 levels.");
            return new { Name = Name(group), Blocks = Items(Property(group, "Blocks")).Select(b => CoreBlockInfo(b, prefix + Name(b))).ToArray(),
                Groups = Items(Property(group, "Groups")).Select(g => CoreGroupHierarchy(g, prefix + Name(g) + "/", depth + 1)).ToArray() };
        }
        [McpServerTool(Name = "GetSoftwareTree"), Description("Read the PLC software hierarchy: blocks, data types and external sources with exact relative paths.")]
        public static object CoreSoftwareTree(string softwarePath)
        {
            lock (Gate)
            {
                var software = Find("software", softwarePath); var rows = new List<Entry>();
                void Walk(object group, string collection, string kind, string prefix, int depth)
                {
                    if (depth > 64) throw new InvalidOperationException("Software tree exceeds 64 levels.");
                    foreach (var item in Items(Property(group, collection))) rows.Add(Row(item, kind, prefix + Name(item)));
                    foreach (var child in Items(Property(group, "Groups"))) Walk(child, collection, kind, prefix + Name(child) + "/", depth + 1);
                }
                Walk(Property(software, "BlockGroup")!, "Blocks", "block", "", 0);
                Walk(Property(software, "TypeGroup")!, "Types", "type", "", 0);
                var sources = Property(software, "ExternalSourceGroup")!;
                foreach (var source in Items(Property(sources, "ExternalSources"))) rows.Add(Row(source, "externalSource", Name(source)));
                return new { Tree = string.Join("\n", rows.Select(r => r.kind + ": " + r.path)), Paths = rows, Meta = new { success = true } };
            }
        }
        private static object CoreExportBlock(string softwarePath, string blockPath, string exportPath, bool preservePath)
        {
            var block = Block(softwarePath, blockPath);
            if (!Path.IsPathRooted(exportPath)) throw new ArgumentException("exportPath must be absolute.");
            string output;
            if (string.Equals(Path.GetExtension(exportPath), ".xml", StringComparison.OrdinalIgnoreCase)) output = Path.GetFullPath(exportPath);
            else
            {
                var directory = Path.GetFullPath(exportPath);
                if (preservePath)
                    foreach (var segment in blockPath.Split('/').Take(blockPath.Split('/').Length - 1)) directory = Path.Combine(directory, CoreFileSegment(segment));
                output = Path.Combine(directory, CoreFileSegment(Name(block)) + ".xml");
            }
            if (File.Exists(output)) throw new IOException("Export destination already exists: " + output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            Invoke(block, "Export", new FileInfo(output), Enum.Parse(Type("ExportOptions"), "WithDefaults"));
            var file = new FileInfo(output);
            if (!file.Exists || file.Length == 0) throw new IOException("SDK produced no nonempty XML file.");
            using (var reader = XmlReader.Create(output, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) while (reader.Read()) { }
            string hash;
            using (var stream = File.OpenRead(output)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            return new { Succeeded = true, ExportPath = output, files = new[] { new { path = output, bytes = file.Length, sha256 = hash } }, Meta = new { success = true } };
        }
        private static string CoreFileSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.Any(c => Path.GetInvalidFileNameChars().Contains(c)) || value.EndsWith(".") || value.EndsWith(" "))
                throw new ArgumentException("Engineering name is not a portable filename; use an explicit XML path.");
            return value;
        }
        private static void CoreSafetyLogin(string softwarePath, string password)
        {
            if (string.IsNullOrEmpty(password)) return;
            var item = Find("deviceItem", softwarePath);
            var admin = Service(item, "Safety.SafetyAdministration") ?? throw new NotSupportedException("Selected device has no SafetyAdministration service.");
            if (!Convert.ToBoolean(Property(admin, "IsLoggedOnToSafetyOfflineProgram")))
                using (var secret = new NetworkCredential("", password).SecurePassword) Invoke(admin, "LoginToSafetyOfflineProgram", secret);
        }
    }
}
