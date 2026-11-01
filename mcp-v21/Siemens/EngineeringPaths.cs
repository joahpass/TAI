using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaMcpServer.Siemens
{
    // Tool paths are literal engineering names, not labels from the rendered tree.
    public sealed class EngineeringPath
    {
        [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
        [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
        [JsonPropertyName("devicePath")] public string DevicePath { get; set; } = string.Empty;
        [JsonPropertyName("deviceItemPath")] public string? DeviceItemPath { get; set; }
        [JsonPropertyName("softwarePath")] public string? SoftwarePath { get; set; }
        [JsonPropertyName("ambiguous")] public bool Ambiguous { get; set; }
        [JsonIgnore] internal object Target { get; set; } = null!;
    }

    public static class EngineeringPaths
    {
        public const string Contract = "Copy exact paths from paths, choosing kind device, deviceItem or software. Paths are case-sensitive [group/]device[/deviceItem/...]. Software uses its owning deviceItem path; never append display labels such as PlcSoftware. Project/Devices prefixes are accepted only when the complete path is unique. Ambiguous paths are rejected.";

        // Pure matching is independently testable without creating or attaching to TIA.
        // Compare every interpretation together: a prefix may also be a real group name.
        public static int ResolveIndex(string requested, string projectName, string kind, string[] canonicalPaths)
        {
            var matches = new List<int>();
            if (!string.IsNullOrWhiteSpace(requested))
                for (var i = 0; i < canonicalPaths.Length; i++)
                {
                    var path = canonicalPaths[i];
                    if (requested == path || requested == "Devices/" + path ||
                        requested == projectName + "/" + path || requested == projectName + "/Devices/" + path)
                        matches.Add(i);
                }
            if (matches.Count == 1) return matches[0];
            var candidates = (matches.Count > 1 ? matches.Select(i => canonicalPaths[i]) : canonicalPaths)
                .Distinct(StringComparer.Ordinal).Take(20).Select(p => "'" + p + "'");
            throw new ArgumentException((matches.Count > 1 ? "Ambiguous " : "No matching ") + kind +
                " path '" + requested + "'. " +
                (kind == "deviceItem" ? "A device path is not a deviceItem path. " : string.Empty) +
                "Valid canonical " + kind + " candidates (up to 20): " + string.Join(", ", candidates) +
                ". Copy the exact path and kind from GetProjectTree.paths or GetDevices.paths; do not append display-tree software labels.");
        }

        internal static List<EngineeringPath> Inventory(ProjectBase project)
        {
            var paths = new List<EngineeringPath>();
            var seenDevices = new HashSet<Device>();
            AddDevices(project.Devices, string.Empty, paths, seenDevices);
            AddGroups(project.DeviceGroups, string.Empty, paths, seenDevices);
            // The system group is a display section, not a user group in a tool path.
            if (project.UngroupedDevicesGroup != null)
                AddDevices(project.UngroupedDevicesGroup.Devices, string.Empty, paths, seenDevices);
            foreach (var group in paths.GroupBy(p => new { p.Kind, p.Path }).Where(g => g.Count() > 1))
                foreach (var entry in group) entry.Ambiguous = true;
            return paths;
        }

        private static void AddGroups(DeviceUserGroupComposition groups, string prefix, List<EngineeringPath> paths, HashSet<Device> seen)
        {
            foreach (var group in groups)
            {
                var next = prefix + group.Name + "/";
                AddDevices(group.Devices, next, paths, seen);
                AddGroups(group.Groups, next, paths, seen);
            }
        }

        private static void AddDevices(DeviceComposition devices, string prefix, List<EngineeringPath> paths, HashSet<Device> seen)
        {
            foreach (var device in devices)
            {
                if (!seen.Add(device)) continue;
                var path = prefix + device.Name;
                paths.Add(new EngineeringPath { Kind = "device", Path = path, DevicePath = path,
                    Name = device.Name, Type = device.GetType().Name, Target = device });
                foreach (var item in device.DeviceItems) AddItem(item, path, path, paths);
            }
        }

        private static void AddItem(DeviceItem item, string parentPath, string devicePath, List<EngineeringPath> paths)
        {
            var path = parentPath + "/" + item.Name;
            paths.Add(new EngineeringPath { Kind = "deviceItem", Path = path, DevicePath = devicePath,
                DeviceItemPath = path, Name = item.Name, Type = item.GetType().Name, Target = item });
            var container = item.GetService<SoftwareContainer>();
            var software = container?.Software;
            if (software != null)
                paths.Add(new EngineeringPath { Kind = "software", Path = path, DevicePath = devicePath,
                    DeviceItemPath = path, SoftwarePath = path, Name = software.Name,
                    Type = software.GetType().Name, Target = container! });
            foreach (var child in item.DeviceItems) AddItem(child, path, devicePath, paths);
        }
    }
}
