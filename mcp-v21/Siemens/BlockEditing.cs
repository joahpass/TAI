using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace TiaMcpServer.Siemens
{
    public sealed class BlockBackupFile
    {
        public string path { get; set; } = "";
        public long bytes { get; set; }
        public string sha256 { get; set; } = "";
    }

    public sealed class BlockBackupEntry
    {
        public string blockPath { get; set; } = "";
        public bool Succeeded { get; set; }
        public string format { get; set; } = "";
        public string error { get; set; } = "";
        public List<BlockBackupFile> files { get; set; } = new List<BlockBackupFile>();
    }

    public sealed class BlockEditResult
    {
        public bool Succeeded { get; set; }
        public JsonObject Meta { get; set; } = new JsonObject { ["success"] = false };
        public string Message { get; set; } = "";
        public string softwarePath { get; set; } = "";
        public string blockPath { get; set; } = "";
        public string newBlockPath { get; set; } = "";
        public string backupPath { get; set; } = "";
        public string backupScope { get; set; } = "target";
        public bool complete { get; set; }
        public bool mutationAttempted { get; set; }
        public bool postconditionVerified { get; set; }
        public int totalBlocks { get; set; }
        public List<BlockBackupEntry> backups { get; set; } = new List<BlockBackupEntry>();
        public List<string> blockers { get; set; } = new List<string>();
    }

    public static class BlockEditing
    {
        private sealed class Target
        {
            public PlcBlock Block = null!;
            public string Path = "";
            public bool Restricted;
            public DateTime Modified;
        }

        // Literal names may contain regex punctuation (e.g. dots); no regex lookup is used.
        public static void ValidateLiteralPath(string value, string argument)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
                value.IndexOfAny(new[] { '*', '?', '[', ']', '\\' }) >= 0 ||
                value.Any(char.IsControl) || value.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new ArgumentException(argument + " must be an exact nonempty slash-separated path without wildcards, empty segments or traversal.");
        }

        public static void ValidateNewName(string name)
        {
            ValidateLiteralPath(name, "newName");
            if (name.Contains("/")) throw new ArgumentException("newName must be one literal block name, not a path.");
        }

        public static BlockEditResult DeleteBlock(Portal portal, string softwarePath, string blockPath, string backupPath)
            => Edit(portal, softwarePath, blockPath, null, backupPath);

        public static BlockEditResult RenameBlock(Portal portal, string softwarePath, string blockPath, string newName, string backupPath)
            => Edit(portal, softwarePath, blockPath, newName ?? "", backupPath);

        private static BlockEditResult Edit(Portal portal, string softwarePath, string blockPath, string? newName, string backupPath)
        {
            var result = new BlockEditResult { softwarePath = softwarePath, blockPath = blockPath };
            try
            {
                ValidateLiteralPath(blockPath, "blockPath");
                if (newName != null) ValidateNewName(newName);
                var software = ResolveSoftware(portal, softwarePath);
                var targets = Enumerate(software);
                var target = Find(targets, software, blockPath);
                CheckEditable(target);
                CheckRename(targets, target, newName);
                result.blockPath = target.Path;
                result.backupPath = CreateBackupDirectory(backupPath);
                result.totalBlocks = 1;
                var backup = Backup(target, result.backupPath, 0);
                result.backups.Add(backup);
                result.complete = backup.Succeeded;
                WriteManifest(result);
                if (!backup.Succeeded) throw new InvalidOperationException("Target backup failed: " + backup.error);

                // Re-resolve and revalidate after export; do not edit a replaced target.
                var currentSoftware = ResolveSoftware(portal, softwarePath);
                if (!currentSoftware.Equals(software)) throw new InvalidOperationException("Software changed during backup.");
                var currentTargets = Enumerate(currentSoftware);
                var current = Find(currentTargets, software, target.Path);
                if (!current.Block.Equals(target.Block) || current.Modified != target.Modified) throw new InvalidOperationException("Target identity or modification timestamp changed during backup.");
                CheckEditable(current);
                CheckRename(currentTargets, current, newName);
                VerifyBackupFiles(backup);
                result.mutationAttempted = true;
                if (newName == null) current.Block.Delete();
                else current.Block.Name = newName;

                var after = Enumerate(software);
                if (newName == null)
                {
                    if (after.Any(t => t.Path == target.Path)) throw new InvalidOperationException("Delete returned but the original block path still exists.");
                }
                else
                {
                    var slash = target.Path.LastIndexOf('/');
                    result.newBlockPath = (slash < 0 ? "" : target.Path.Substring(0, slash + 1)) + newName;
                    var renamed = after.SingleOrDefault(t => t.Path == result.newBlockPath);
                    if (renamed == null || !renamed.Block.Equals(current.Block) || after.Any(t => t.Path == target.Path))
                        throw new InvalidOperationException("Rename returned but old/new path and target identity postconditions failed.");
                }
                result.postconditionVerified = true;
                Success(result, newName == null ? "Block deleted; target backup verified. Compile and save separately." : "Block renamed; target backup verified. Validate references, compile and save separately.");
            }
            catch (Exception ex) { Failure(result, ex); }
            return result;
        }

        public static BlockEditResult BackupSoftwareForEdit(Portal portal, string softwarePath, string backupPath)
        {
            var result = new BlockEditResult { softwarePath = softwarePath, backupScope = "software-blocks" };
            try
            {
                var software = ResolveSoftware(portal, softwarePath);
                var targets = Enumerate(software); // Enumeration exceptions must never become an empty success.
                result.totalBlocks = targets.Count;
                result.backupPath = CreateBackupDirectory(backupPath);
                for (var i = 0; i < targets.Count; i++)
                {
                    var entry = Backup(targets[i], result.backupPath, i);
                    result.backups.Add(entry);
                    if (!entry.Succeeded) result.blockers.Add(entry.blockPath + ": " + entry.error);
                }
                var after = Enumerate(ResolveSoftware(portal, softwarePath));
                if (targets.Count != after.Count || targets.Any(t => !after.Any(a => a.Path == t.Path && a.Block.Equals(t.Block) && a.Modified == t.Modified)))
                    result.blockers.Add("Block inventory or modification timestamps changed during backup; retry with engineering edits paused.");
                foreach (var entry in result.backups.Where(b => b.Succeeded)) VerifyBackupFiles(entry);
                result.complete = result.blockers.Count == 0 && result.backups.All(b => b.Succeeded);
                WriteManifest(result);
                if (result.complete) Success(result, "Every enumerated block has a verified backup. This does not back up tags, PLC types, hardware or other project data.");
                else result.Message = "Backup incomplete; resolve each blocker before relying on this software backup.";
            }
            catch (Exception ex) { result.complete = false; Failure(result, ex); }
            return result;
        }

        private static PlcSoftware ResolveSoftware(Portal portal, string path)
        {
            path = path?.Replace('\\', '/') ?? "";
            ValidateLiteralPath(path, "softwarePath");
            if (!portal.IsConnected() || !portal.ProjectIsValid) throw new InvalidOperationException("Connect to an existing project first.");
            // Use the same exact inventory resolver as query/import tools, including ungrouped devices.
            return portal.GetSoftware(path) as PlcSoftware
                ?? throw new InvalidOperationException("The resolved software is not PLC software. Copy a PLC path from GetProjectTree.paths.");
        }

        private static void MatchGroups(DeviceUserGroupComposition groups, string prefix, string path, List<PlcSoftware> matches)
        {
            foreach (var group in groups)
            {
                var next = prefix + group.Name + "/";
                MatchDevices(group.Devices, next, path, matches);
                MatchGroups(group.Groups, next, path, matches);
            }
        }

        private static void MatchDevices(DeviceComposition devices, string prefix, string path, List<PlcSoftware> matches)
        {
            foreach (var device in devices)
                foreach (var item in device.DeviceItems)
                {
                    MatchItem(item, prefix, path, matches);
                    MatchItem(item, prefix + device.Name + "/", path, matches);
                }
        }

        private static void MatchItem(DeviceItem item, string prefix, string path, List<PlcSoftware> matches)
        {
            var itemPath = prefix + item.Name;
            if (itemPath == path && item.GetService<SoftwareContainer>()?.Software is PlcSoftware software) matches.Add(software);
            foreach (var child in item.DeviceItems) MatchItem(child, itemPath + "/", path, matches);
        }

        private static List<Target> Enumerate(PlcSoftware software)
        {
            var targets = new List<Target>();
            AddGroup(software.BlockGroup, "", false, targets);
            var units = software.GetService<PlcUnitProvider>();
            if (units != null)
            {
                foreach (var unit in units.UnitGroup.Units) AddGroup(unit.BlockGroup, "@units/" + unit.Name + "/", false, targets);
                foreach (var unit in units.UnitGroup.SafetyUnits) AddGroup(unit.BlockGroup, "@safetyUnits/" + unit.Name + "/", true, targets);
            }
            AddTechnology(software.TechnologicalObjectGroup, "@technology/", targets);
            if (targets.GroupBy(t => t.Path, StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new InvalidOperationException("Ambiguous canonical block paths in software inventory.");
            return targets;
        }

        private static void AddGroup(PlcBlockGroup group, string prefix, bool restricted, List<Target> targets)
        {
            foreach (var block in group.Blocks) targets.Add(new Target { Block = block, Modified = block.ModifiedDate, Path = prefix + block.Name, Restricted = restricted });
            foreach (var child in group.Groups) AddGroup(child, prefix + child.Name + "/", restricted, targets);
            if (group is PlcBlockSystemGroup root)
                foreach (var child in root.SystemBlockGroups) AddSystemGroup(child, prefix + "@system/" + child.Name + "/", targets);
        }

        private static void AddSystemGroup(PlcSystemBlockGroup group, string prefix, List<Target> targets)
        {
            foreach (var block in group.Blocks) targets.Add(new Target { Block = block, Modified = block.ModifiedDate, Path = prefix + block.Name, Restricted = true });
            foreach (var child in group.Groups) AddSystemGroup(child, prefix + child.Name + "/", targets);
        }

        private static void AddTechnology(TechnologicalInstanceDBGroup group, string prefix, List<Target> targets)
        {
            foreach (var block in group.TechnologicalObjects) targets.Add(new Target { Block = block, Modified = block.ModifiedDate, Path = prefix + block.Name, Restricted = true });
            foreach (var child in group.Groups) AddTechnology(child, prefix + child.Name + "/", targets);
        }

        private static Target Find(List<Target> targets, PlcSoftware software, string path)
        {
            var rootPrefix = software.BlockGroup.Name + "/";
            var matches = targets.Where(t => t.Path == path || rootPrefix + t.Path == path).ToList();
            if (matches.Count != 1) throw new InvalidOperationException("Exact blockPath must match one block; found " + matches.Count + ". Paths are case-sensitive and relative to Program blocks (optional actual root name).");
            return matches[0];
        }

        private static void CheckEditable(Target target)
        {
            var block = target.Block;
            var language = block.ProgrammingLanguage.ToString();
            var safetyOrganizationBlock = block is OB organizationBlock && organizationBlock.SecondaryType.ToString().IndexOf("Safety", StringComparison.OrdinalIgnoreCase) >= 0;
            if (target.Restricted || safetyOrganizationBlock || language.StartsWith("F_", StringComparison.Ordinal) || language == "FCP" || language == "FLD" || language == "CPU_DB" || language == "SDB")
                throw new InvalidOperationException("Safety, system-generated and technology blocks cannot be edited by this tool: " + target.Path);
            if (block.IsKnowHowProtected) throw new InvalidOperationException("Know-how protected block: " + target.Path);
            var writeProtection = block.GetService<PlcBlockWriteProtectionProvider>();
            if (writeProtection != null && (writeProtection.IsProtected || writeProtection.IsDefined))
                throw new InvalidOperationException("Write protection is defined or active for block: " + target.Path);
        }

        private static void CheckRename(List<Target> targets, Target target, string? name)
        {
            if (name == null) return;
            if (string.Equals(target.Block.Name, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("New name must differ from the existing name beyond case.");
            if (targets.Any(t => string.Equals(t.Block.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A block with the proposed name already exists in this software: " + name);
        }

        private static string CreateBackupDirectory(string parent)
        {
            if (string.IsNullOrWhiteSpace(parent) || !Path.IsPathRooted(parent) || Path.GetPathRoot(parent)?.Length < 3)
                throw new ArgumentException("backupPath must be an absolute directory path.");
            var directory = Path.Combine(Path.GetFullPath(parent), "block-edit-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static BlockBackupEntry Backup(Target target, string root, int index)
        {
            var entry = new BlockBackupEntry { blockPath = target.Path };
            var errors = new List<string>();
            try
            {
                var directory = Path.Combine(root, index.ToString("D6"));
                Directory.CreateDirectory(directory);
                if (target.Block.IsConsistent)
                {
                    try
                    {
                        var xml = Path.Combine(directory, "block.xml");
                        target.Block.Export(new FileInfo(xml), ExportOptions.WithDefaults);
                        XDocument.Load(xml); // Reject empty, truncated and malformed XML.
                        entry.files.Add(InspectFile(xml, directory));
                        entry.format = "xml";
                        entry.Succeeded = true;
                        return entry;
                    }
                    catch (Exception ex) { errors.Add("XML: " + Detail(ex)); }
                }
                else errors.Add("XML: block is inconsistent; document fallback required.");
                var documents = Path.Combine(directory, "documents");
                Directory.CreateDirectory(documents);
                var exported = target.Block.ExportAsDocuments(new DirectoryInfo(documents), "block");
                if (exported == null) throw new InvalidOperationException("ExportAsDocuments returned no result.");
                if (exported.State != DocumentResultState.Success)
                    throw new InvalidOperationException("ExportAsDocuments state " + exported.State + ": " + string.Join("; ", exported.Messages.Select(m => m.Message)));
                var files = exported.ExportedDocuments.ToList();
                if (files.Count == 0) throw new InvalidOperationException("ExportAsDocuments returned success without exported document files.");
                foreach (var file in files) entry.files.Add(InspectFile(file.FullName, documents));
                entry.format = "documents";
                entry.Succeeded = true;
            }
            catch (Exception ex) { errors.Add("Backup: " + Detail(ex)); entry.error = string.Join(" | ", errors); }
            return entry;
        }

        private static BlockBackupFile InspectFile(string path, string directory)
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Export reported a file outside its new backup directory: " + full);
            using (var stream = File.OpenRead(full))
            {
                if (stream.Length == 0) throw new InvalidOperationException("Export produced an empty file: " + full);
                using (var hash = SHA256.Create())
                    return new BlockBackupFile { path = full, bytes = stream.Length, sha256 = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() };
            }
        }

        private static void VerifyBackupFiles(BlockBackupEntry backup)
        {
            foreach (var file in backup.files)
            {
                var actual = InspectFile(file.path, Path.GetDirectoryName(file.path)!);
                if (actual.bytes != file.bytes || actual.sha256 != file.sha256) throw new IOException("Backup changed before edit: " + file.path);
            }
        }

        private static void WriteManifest(BlockEditResult result)
            => File.WriteAllText(Path.Combine(result.backupPath, "manifest.json"), JsonSerializer.Serialize(new { result.softwarePath, result.blockPath, result.backupScope, result.complete, result.totalBlocks, result.backups, result.blockers }, new JsonSerializerOptions { WriteIndented = true }));

        private static void Success(BlockEditResult result, string message)
        {
            result.Succeeded = true;
            result.Meta["success"] = true;
            result.Message = message;
        }

        private static string Detail(Exception ex) => ex.GetType().Name + ": " + ex.Message;
        private static void Failure(BlockEditResult result, Exception ex)
        {
            result.Succeeded = false;
            result.Meta["success"] = false;
            result.Message = Detail(ex) + (result.mutationAttempted ? " Modification was attempted; inspect the current project and backup before retrying. No automatic rollback was attempted." : " No block modification was attempted.");
            result.blockers.Add(Detail(ex));
        }
    }
}
