using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using TiaMcpServer.Siemens;

var checks = 0;
void Check(bool pass, string reason) { if (!pass) throw new Exception(reason); checks++; }
void Reject(string input, string kind, string[] candidates, string reason)
{
    try { EngineeringPaths.ResolveIndex(input, "交通灯_V21", kind, candidates); throw new Exception("Accepted " + input); }
    catch (ArgumentException ex)
    {
        Check(ex.Message.Contains(reason), "Expected failure reason: " + ex.Message);
        Check(ex.Message.Contains("GetProjectTree.paths"), "Recovery instruction missing");
        foreach (var candidate in candidates.Distinct()) Check(ex.Message.Contains(candidate), "Candidate missing");
    }
}
var plc = "S7-1200 station_1/PLC_1";
foreach (var path in new[] { plc, "Devices/" + plc, "交通灯_V21/" + plc, "交通灯_V21/Devices/" + plc })
    Check(EngineeringPaths.ResolveIndex(path, "交通灯_V21", "software", new[] { plc }) == 0, "Prefix resolution: " + path);
foreach (var suffix in new[] { "/PlcSoftware: PLC_1", "/PlcSoftware", "/PlcSoftware:PLC_1", "/PlcSoftware/PLC_1", "/extra", "/" })
    Reject(plc + suffix, "software", new[] { plc }, "No matching");
foreach (var path in new[] { "PLC_1", "s7-1200 station_1/PLC_1", "*/PLC_1", "S7-1200 station_1//PLC_1", "", "OtherProject/Devices/" + plc })
    Reject(path, "software", new[] { plc }, "No matching");
Reject("HMI_1", "deviceItem", new[] { "HMI_1/HMI runtime" }, "A device path is not a deviceItem path");
Reject("Devices/" + plc, "software", new[] { plc, "Devices/" + plc }, "Ambiguous");
Reject("交通灯_V21/" + plc, "software", new[] { plc, "交通灯_V21/" + plc }, "Ambiguous");
Reject(plc, "software", new[] { plc, plc }, "Ambiguous");
Check(EngineeringPaths.ResolveIndex("Group/Sub/Device/CPU", "P", "software", new[] { "Other/Device/CPU", "Group/Sub/Device/CPU" }) == 1, "Nested exact group selection");
Check(EngineeringPaths.ResolveIndex("S7-1500/ET200MP-Station_1/CPU", "P", "software", new[] { "S7-1500/ET200MP-Station_1/CPU" }) == 0, "Literal slash in device name");

Device Hardware(string name, Software software, bool nested = false)
{
    var device = new Device { Name = name };
    var cpu = new DeviceItem { Name = software.Name, Container = new SoftwareContainer { Software = software } };
    if (nested) { var rack = new DeviceItem { Name = "Rack" }; rack.DeviceItems.Add(cpu); device.DeviceItems.Add(rack); }
    else device.DeviceItems.Add(cpu);
    return device;
}
var project = new ProjectBase { UngroupedDevicesGroup = new DeviceSystemGroup() };
var station = Hardware("S7-1200 station_1", new PlcSoftware { Name = "PLC_1" });
project.Devices.Add(station);
project.UngroupedDevicesGroup.Devices.Add(station); // SDK may expose the same device in multiple compositions.
project.UngroupedDevicesGroup.Devices.Add(Hardware("HMI_1", new HmiTarget { Name = "Classic" }));
var group = new DeviceUserGroup { Name = "Group" };
var sub = new DeviceUserGroup { Name = "Sub" };
group.Groups.Add(sub);
project.DeviceGroups.Add(group);
sub.Devices.Add(Hardware("Unified", new HmiSoftware { Name = "Runtime" }, true));
var inventory = EngineeringPaths.Inventory(project);
Check(inventory.Count(p => p.Kind == "device") == 3, "All devices, including ungrouped, deduplicated");
Check(inventory.Count(p => p.Kind == "software") == 3, "Generic PLC + Classic + Unified discovery");
Check(inventory.Any(p => p.Kind == "deviceItem" && p.Path == "Group/Sub/Unified/Rack/Runtime"), "Recursive nested item inventory");
foreach (var kind in new[] { "device", "deviceItem", "software" })
{
    var entries = inventory.Where(p => p.Kind == kind).ToArray();
    for (var i = 0; i < entries.Length; i++)
        Check(EngineeringPaths.ResolveIndex(entries[i].Path, "P", kind, entries.Select(p => p.Path).ToArray()) == i, "Inventory round trip " + entries[i].Path);
}
var generic = inventory.Single(p => p.Type == "HmiTarget");
Check(generic.Path == generic.SoftwarePath && generic.Path == generic.DeviceItemPath, "Software uses owner item path");
Check(generic.DevicePath == "HMI_1", "Device vs item explicit");
var json = JsonSerializer.Serialize(inventory);
Check(json.Contains("\"kind\":\"software\"") && json.Contains("\"softwarePath\""), "Readable serialized contract");
Check(!json.Contains("\"Target\"") && !json.Contains("\"Container\""), "No SDK objects serialized");
project.Devices.Add(Hardware("S7-1200 station_1", new PlcSoftware { Name = "PLC_1" }));
Check(EngineeringPaths.Inventory(project).Where(p => p.Path == plc).All(p => p.Ambiguous), "Duplicate canonical paths flagged");
Console.WriteLine($"PASS: {checks} offline canonical path and inventory checks; no TIA connection or project writes.");
