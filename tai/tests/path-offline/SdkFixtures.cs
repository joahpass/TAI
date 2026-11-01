// Minimal public-contract fixtures. No Siemens assembly, TiaPortal or live project is loaded.
namespace Siemens.Engineering
{
    public class ProjectBase
    {
        public HW.DeviceComposition Devices { get; } = new();
        public HW.DeviceUserGroupComposition DeviceGroups { get; } = new();
        public HW.DeviceSystemGroup? UngroupedDevicesGroup { get; set; }
    }
}
namespace Siemens.Engineering.HW
{
    public class Software { public string Name { get; set; } = ""; }
    public class PlcSoftware : Software { }
    public class HmiTarget : Software { }
    public class HmiSoftware : Software { }
    public class DeviceComposition : List<Device> { }
    public class DeviceUserGroupComposition : List<DeviceUserGroup> { }
    public class DeviceSystemGroup { public DeviceComposition Devices { get; } = new(); }
    public class DeviceUserGroup
    {
        public string Name { get; set; } = "";
        public DeviceComposition Devices { get; } = new();
        public DeviceUserGroupComposition Groups { get; } = new();
    }
    public class Device
    {
        public string Name { get; set; } = "";
        public List<DeviceItem> DeviceItems { get; } = new();
    }
    public class DeviceItem
    {
        public string Name { get; set; } = "";
        public List<DeviceItem> DeviceItems { get; } = new();
        public Features.SoftwareContainer? Container { get; set; }
        public T? GetService<T>() where T : class => Container as T;
    }
}
namespace Siemens.Engineering.HW.Features
{
    public class SoftwareContainer { public Software? Software { get; set; } }
}
