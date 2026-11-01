using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;

class Program
{
    static Type tools = null!;
    static int checks;
    static object Call(string name, params object[] args) { try { return tools.GetMethod(name)!.Invoke(null, args)!; } catch (TargetInvocationException e) { throw e.InnerException!; } }
    static object? Get(object value, string name) => value is IDictionary dict ? dict[name] : value.GetType().GetProperty(name)!.GetValue(value);
    static object[] ArrayOf(object? value) => ((IEnumerable)value!).Cast<object>().ToArray();
    static void Check(bool pass, string reason) { checks++; if (!pass) throw new Exception(reason); }
    static void Fails(Action action, string reason) { try { action(); } catch { checks++; return; } throw new Exception("Expected failure: " + reason); }
    static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            tools = assembly.GetType("TiaMcpServer.Compatibility.V19Tools", true)!;
            tools.GetField("sdk", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(ExportOptions).Assembly);
            var portal = new Portal();
            tools.GetField("portal", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, portal);
            Check(Equals(Get(Call("State"), "IsConnected"), true), "connected without project");
            Check(Get(Call("State"), "Project") == null, "no project state");
            var project = new Project(portal) { Name = "Fixture" };
            portal.Projects.Add(project);
            var software = new Software { Name = "PLC" };
            var device = new Device { Name = "Device" };
            var item = new DeviceItem { Name = "CPU", Container = new Siemens.Engineering.HW.Features.SoftwareContainer { Software = software } };
            device.DeviceItems.Add(item); project.Devices.Add(device);
            software.BlockGroup.Name = "Program blocks";
            var child = new BlockGroup { Name = "Folder" }; software.BlockGroup.Groups.Add(child);
            var block = new Block { Name = "Logic" }; child.Blocks.Add(block);
            software.TypeGroup.Types.Add(new EngineeringObject { Name = "DataType" });
            software.ExternalSourceGroup.ExternalSources.Add(new EngineeringObject { Name = "Source" });
            Check(ArrayOf(Get(Call("Devices"), "Items")).Length == 1, "devices Items contract");
            Check(ArrayOf(Get(Call("Tree"), "Paths")).Length == 3, "canonical device/item/software inventory");
            var blockResult = Call("GetBlocks", "Device/CPU", "^Logic$");
            var blockInfo = ArrayOf(Get(blockResult, "Items")).Single();
            Check(Equals(Get(blockInfo, "Name"), "Logic"), "block Name contract");
            Check(Equals(Get(blockInfo, "path"), "Folder/Logic"), "block relative path");
            Check(ArrayOf(Get(blockInfo, "Attributes")).Length == 1, "explicit interface attribute access");
            Check(ArrayOf(Get(Call("GetBlocks", "Device/CPU", "^Other$"), "Items")).Length == 0, "regex filter");
            Fails(() => Call("GetBlocks", "Device/CPU", "["), "invalid regex");
            Check(Equals(Get(Call("BlockInfo", "Device/CPU", "Program blocks/Folder/Logic"), "Name"), "Logic"), "root-prefixed block path");
            Check(Get(Call("CoreSoftwareTree", "Device/CPU"), "Tree")!.ToString()!.Contains("externalSource: Source"), "software source tree");
            Check(ArrayOf(Get(Call("CoreSoftwareTree", "Device/CPU"), "Paths")).Length == 3, "blocks types sources all present");
            var hierarchy = Get(Call("CoreHierarchy", "Device/CPU"), "Root")!;
            Check(ArrayOf(Get(hierarchy, "Groups")).Length == 1, "nested hierarchy");
            Check(Equals(Get(Call("CoreDeviceItemInfo", "Device/CPU"), "Name"), "CPU"), "device item info");
            item.BadAttribute = true;
            var bad = Call("CoreDeviceItemInfo", "Device/CPU");
            Check(Equals(Get(Get(bad,"Meta")!,"partial"),true),"attribute failure preserved");
            item.BadAttribute = false;
            var root = Path.Combine(Path.GetTempPath(), "TAI-v19-core-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var exported = Call("ExportBlock", "Device/CPU", "Folder/Logic", Path.Combine(root,"exports"), true);
            Check(File.Exists(Path.Combine(root,"exports","Folder","Logic.xml")),"directory export preserves groups");
            Check(Get(exported,"ExportPath")!.ToString()!.EndsWith("Logic.xml"),"export actual path");
            Fails(() => Call("ExportBlock", "Device/CPU", "Folder/Logic", Path.Combine(root,"exports"), true),"no overwrite");
            Check(Equals(Get(Call("Compile","Device/CPU",""),"Succeeded"),true),"positive compiler state");
            software.Compiler.Result.State="Error";
            Check(Equals(Get(Call("Compile","Device/CPU",""),"Succeeded"),false),"error state with zero count stays failure");
            software.Compiler.Result.State="Success"; software.Compiler.Result.ErrorCount=1;
            Check(Equals(Get(Call("Compile","Device/CPU",""),"Succeeded"),false),"nonzero errors stay failure");
            software.Compiler.Result.Messages.Add(new Siemens.Engineering.Compiler.Message { Path="Folder/Logic", ErrorCount=1 });
            var message=ArrayOf(Get(Call("Compile","Device/CPU",""),"Messages")).Single();
            Check(Equals(Get(message,"Path"),"Folder/Logic"),"compile message path");
            Call("Save"); Check(project.SaveCount==1,"local project save");
            Call("CoreSaveAs",Path.Combine(root,"SavedAs")); Check(project.SaveAsCount==1,"save as directory");
            Call("CoreCloseProject"); Check(portal.Projects.Count==0,"explicit close");
            string future=Path.Combine(root,"future.ap21"); File.WriteAllText(future,"fixture");
            Fails(()=>Call("CoreOpenProject",future),"future version rejected before API");
            string local=Path.Combine(root,"new.ap19"); File.WriteAllText(local,"fixture");
            Call("CoreOpenProject",local); Check(portal.Projects.Count==1,"local open");
            string other=Path.Combine(root,"other.ap19"); File.WriteAllText(other,"fixture");
            Fails(()=>Call("CoreOpenProject",other),"do not implicitly close current project");
            Call("CoreCloseProject");
            string sessionPath=Path.Combine(root,"session.als19"); File.WriteAllText(sessionPath,"fixture");
            Call("CoreOpenProject",sessionPath);
            Check(Equals(Get(Call("State"),"IsLocalSession"),true),"session state");
            Check(Get(Call("State"),"Session")!=null,"session identity");
            var session=portal.LocalSessions.Single(); Call("Save"); Check(session.SaveCount==1,"session save uses LocalSession");
            Fails(()=>Call("CoreSaveAs",Path.Combine(root,"SessionCopy")),"session SaveAs unsupported");
            Check(ArrayOf(Get(Call("GetProject"),"Items")).Length==1,"session appears in project list");
            Call("CoreCloseProject"); Check(portal.LocalSessions.Count==0,"session closes via LocalSession");
            Console.WriteLine("PASS: "+checks+" V19 core contract, attributes, lifecycle, session, export and compile assertions. Artifacts: "+root);
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
namespace Siemens.Engineering
{
    public enum ExportOptions { WithDefaults }
    public enum AttributeAccess { Read, ReadWrite }
    public class AttributeInfo { public string Name {get;set;}="Name"; public AttributeAccess AccessMode {get;set;}=AttributeAccess.Read; }
    public interface IEngineeringObject { IEnumerable<AttributeInfo> GetAttributeInfos(); object GetAttribute(string name); }
    public interface IEngineeringServiceProvider { T? GetService<T>() where T:class; }
    public class EngineeringObject : IEngineeringObject
    {
        public string Name {get;set;}="Fixture"; public bool BadAttribute;
        IEnumerable<AttributeInfo> IEngineeringObject.GetAttributeInfos() { yield return new AttributeInfo(); if(BadAttribute) yield return new AttributeInfo {Name="Bad"}; }
        object IEngineeringObject.GetAttribute(string name) => name=="Bad" ? throw new InvalidOperationException("read failed") : Name;
    }
    public class Portal { public Projects Projects {get;} public Sessions LocalSessions {get;} public Portal(){Projects=new Projects(this);LocalSessions=new Sessions(this);} }
    public class Projects:List<Project> { readonly Portal owner; public Projects(Portal p){owner=p;} public Project Open(FileInfo file){var p=new Project(owner){Name=file.Name,Path=file};Add(p);return p;} public Project OpenWithUpgrade(FileInfo file)=>Open(file); }
    public class Sessions:List<Session> { readonly Portal owner; public Sessions(Portal p){owner=p;} public Session Open(FileInfo file){var s=new Session(owner){Name=file.Name,Path=file,Project=new Project(owner){Name=file.Name,Path=file}};Add(s);return s;} }
    public class Session:EngineeringObject { readonly Portal owner; public Session(Portal p){owner=p;} public Project Project{get;set;}=null!; public FileInfo Path{get;set;}=new FileInfo("C:/fixture.als19"); public int SaveCount; public void Save(){SaveCount++;} public void Close(){owner.LocalSessions.Remove(this);} }
    public class Project:EngineeringObject { readonly Portal owner; public Project(Portal p){owner=p;} public FileInfo Path{get;set;}=new FileInfo("C:/fixture.ap19"); public List<Device> Devices{get;}=new List<Device>(); public List<DeviceGroup> DeviceGroups{get;}=new List<DeviceGroup>(); public DeviceGroup UngroupedDevicesGroup{get;}=new DeviceGroup(); public int SaveCount,SaveAsCount; public void Save(){SaveCount++;} public void SaveAs(DirectoryInfo d){d.Create();SaveAsCount++;} public void Close(){owner.Projects.Remove(this);} }
    public class DeviceGroup:EngineeringObject {public List<Device> Devices{get;}=new List<Device>(); public List<DeviceGroup> Groups{get;}=new List<DeviceGroup>();}
    public class Device:EngineeringObject {public List<DeviceItem> DeviceItems{get;}=new List<DeviceItem>();}
    public class DeviceItem:EngineeringObject,IEngineeringServiceProvider {public List<DeviceItem> DeviceItems{get;}=new List<DeviceItem>(); public HW.Features.SoftwareContainer? Container; public T? GetService<T>() where T:class => Container as T;}
    public class Software:EngineeringObject,IEngineeringServiceProvider { public BlockGroup BlockGroup{get;}=new BlockGroup(); public TypeGroup TypeGroup{get;}=new TypeGroup(); public SourceGroup ExternalSourceGroup{get;}=new SourceGroup(); public Compiler.Compiler Compiler{get;}=new Compiler.Compiler(); public T? GetService<T>() where T:class => Compiler as T; }
    public class BlockGroup:EngineeringObject { public List<Block> Blocks{get;}=new List<Block>(); public List<BlockGroup> Groups{get;}=new List<BlockGroup>(); }
    public class TypeGroup:EngineeringObject { public List<EngineeringObject> Types{get;}=new List<EngineeringObject>(); public List<TypeGroup> Groups{get;}=new List<TypeGroup>(); }
    public class SourceGroup:EngineeringObject { public List<EngineeringObject> ExternalSources{get;}=new List<EngineeringObject>(); }
    public class Block:EngineeringObject { public string ProgrammingLanguage=>"SCL"; public bool IsConsistent=>true; public void Export(FileInfo file, ExportOptions options){File.WriteAllText(file.FullName,"<Document><SW.Blocks.FB Name='Logic'/></Document>");} }
}
namespace Siemens.Engineering.HW.Features { public class SoftwareContainer { public Siemens.Engineering.Software Software{get;set;}=null!; } }
namespace Siemens.Engineering.Compiler
{
    public interface ICompilable { Result Compile(); }
    public class Compiler:ICompilable { public Result Result{get;}=new Result(); public Result Compile()=>Result; }
    public class Result { public string State{get;set;}="Success"; public int ErrorCount{get;set;} public int WarningCount{get;set;} public List<Message> Messages{get;}=new List<Message>(); }
    public class Message:Result { public string Description{get;set;}="fixture"; public string Path{get;set;}=""; }
}
