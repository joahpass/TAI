using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ModelContextProtocol.Server { [AttributeUsage(AttributeTargets.Method)] public sealed class McpServerToolAttribute : Attribute { public string Name { get; set; } = ""; } }
namespace Siemens.Engineering
{
    public enum ExportOptions { None, WithDefaults }
    public enum ImportOptions { Override }
    public interface IEngineeringObject { IEnumerable<AttributeInfo> GetAttributeInfos(); object GetAttribute(string name); }
    public sealed class AttributeInfo { public string Name { get; set; } = ""; public string AccessMode { get; set; } = "ReadWrite"; }
    public interface IEngineeringServiceProvider { T? GetService<T>() where T : class; }
    public class EngineeringObject : IEngineeringObject, IEngineeringServiceProvider
    {
        public string Name { get; set; } = "";
        public Dictionary<System.Type, object> Services = new Dictionary<System.Type, object>();
        public string AttributeAccess = "ReadWrite";
        public object? AttributeOverride;
        public bool AttributeThrow;
        public T? GetService<T>() where T : class => Services.TryGetValue(typeof(T), out var value) ? (T)value : null;
        public IEnumerable<AttributeInfo> GetAttributeInfos() => new[] { new AttributeInfo { Name = "Name", AccessMode = AttributeAccess } };
        public object GetAttribute(string name) => AttributeThrow ? throw new InvalidOperationException("fake attribute failure") : AttributeOverride ?? (name == "Name" ? Name : throw new ArgumentException(name));
    }
}
namespace Siemens.Engineering.SW
{
    public sealed class PlcSoftware : Siemens.Engineering.EngineeringObject
    {
        public Group BlockGroup { get; set; } = new Group { Name = "Program blocks" };
        public Group TypeGroup { get; set; } = new Group { Name = "PLC data types" };
        public TechnologyGroup TechnologicalObjectGroup { get; set; } = new TechnologyGroup();
        public SourceGroup ExternalSourceGroup { get; set; } = new SourceGroup();
    }
    public sealed class Group : Siemens.Engineering.EngineeringObject
    {
        public ObjectCollection Blocks { get; set; } = new ObjectCollection();
        public ObjectCollection Types { get; set; } = new ObjectCollection();
        public List<Group> Groups { get; set; } = new List<Group>();
        public List<Group> SystemBlockGroups { get; set; } = new List<Group>();
    }
    public sealed class TechnologyGroup
    {
        public string Name { get; set; } = "TO";
        public ObjectCollection TechnologicalObjects { get; set; } = new ObjectCollection();
        public List<TechnologyGroup> Groups { get; set; } = new List<TechnologyGroup>();
    }
    public class PlcObject : Siemens.Engineering.EngineeringObject
    {
        public DateTime ModifiedDate { get; set; } = new DateTime(2026, 1, 1);
        public bool IsConsistent { get; set; } = true;
        public bool IsKnowHowProtected { get; set; }
        public string Namespace { get; set; } = "";
        public string ProgrammingLanguage { get; set; } = "SCL";
        public string MemoryLayout { get; set; } = "Optimized";
        public string HeaderName { get; set; } = "";
        public string SecondaryType { get; set; } = "";
        public ObjectCollection? Owner;
        public string ExportMode = "ok";
        public Action? OnExport;
        public bool DeleteNoop;
        public void Export(FileInfo file, Siemens.Engineering.ExportOptions options)
        {
            if (ExportMode == "throw") throw new IOException("fake export failure");
            if (ExportMode != "missing") File.WriteAllText(file.FullName, ExportMode == "empty" ? "" : ExportMode == "malformed" ? "<Document>" : "<Document><SW.Blocks.FC /></Document>");
            OnExport?.Invoke();
        }
        public void Delete() { if (!DeleteNoop) Owner!.Remove(this); }
    }
    public sealed class ObjectCollection : List<PlcObject>
    {
        public bool ImportEmpty;
        public bool ImportThrow;
        public int ImportCalls;
        public new void Add(PlcObject value) { value.Owner = this; base.Add(value); }
        public IList<PlcObject> Import(FileInfo file, Siemens.Engineering.ImportOptions options)
        {
            ImportCalls++;
            if (ImportThrow) throw new InvalidOperationException("fake import failure");
            if (ImportEmpty) return new List<PlcObject>();
            var item = new PlcObject { Name = Path.GetFileNameWithoutExtension(file.Name) }; Add(item); return new[] { item };
        }
    }
    public sealed class SourceGroup { public SourceCollection ExternalSources { get; set; } = new SourceCollection(); }
    public sealed class SourceCollection : List<Source>
    {
        public bool GenerateEmpty;
        public bool GenerateThrow;
        public Source CreateFromFile(string name, string path) { if (!File.Exists(path)) throw new FileNotFoundException(path); var source = new Source { Name = name, Empty = GenerateEmpty, Throw = GenerateThrow }; Add(source); return source; }
    }
    public sealed class Source : Siemens.Engineering.EngineeringObject
    {
        public bool Empty, Throw;
        public IList<PlcObject> GenerateBlocksFromSource() { if (Throw) throw new InvalidOperationException("generation error"); return Empty ? new List<PlcObject>() : new[] { new PlcObject { Name = "Generated" } }; }
    }
}
namespace Siemens.Engineering.SW.Units
{
    public sealed class PlcUnitProvider { public UnitGroup UnitGroup { get; set; } = new UnitGroup(); }
    public sealed class UnitGroup { public List<Unit> Units { get; set; } = new List<Unit>(); public List<Unit> SafetyUnits { get; set; } = new List<Unit>(); }
    public sealed class Unit { public string Name { get; set; } = "U"; public Siemens.Engineering.SW.Group BlockGroup { get; set; } = new Siemens.Engineering.SW.Group(); }
}
namespace Siemens.Engineering.SW.Blocks { public sealed class PlcBlockWriteProtectionProvider { public bool IsProtected { get; set; } public bool IsDefined { get; set; } } }
namespace TiaMcpServer.Compatibility
{
    // Only the shared bridge boundary is replaced. Every public PLC tool executes the production source linked by the test project.
    public static partial class V19Tools
    {
        private static readonly object Gate = new object();
        private static Assembly Sdk => typeof(Siemens.Engineering.SW.PlcSoftware).Assembly;
        private static System.Type Type(string name) => Sdk.GetType("Siemens.Engineering." + name, true)!;
        public static Siemens.Engineering.SW.PlcSoftware Fixture = new Siemens.Engineering.SW.PlcSoftware();
        private static object Find(string kind, string path) => kind == "software" && path == "PLC/CPU" ? Fixture : throw new ArgumentException("software not found");
        private static object? Property(object value, string name) => (value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new MissingMemberException(value.GetType().FullName, name)).GetValue(value);
        private static object? Invoke(object value, string name, params object[] args)
        {
            var type = value as System.Type ?? value.GetType();
            var flags = BindingFlags.Public | (value is System.Type ? BindingFlags.Static : BindingFlags.Instance);
            var candidates = type.GetMethods(flags).Where(m => m.Name == name && !m.IsGenericMethod && m.GetParameters().Length == args.Length)
                .Where(m => m.GetParameters().Select((p, i) => args[i] == null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x)).ToArray();
            if (candidates.Length != 1) throw new MissingMethodException(type.FullName, name + " (exact public signature)");
            try { return candidates[0].Invoke(value is System.Type ? null : value, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }
        private static IEnumerable<object> Items(object? value) => value is IEnumerable sequence ? sequence.Cast<object>() : throw new InvalidOperationException("Expected a public SDK collection.");
        private static string Name(object value) => Convert.ToString(Property(value, "Name")) ?? "";
        private static object? Service(object value, string name)
        {
            var provider = Type("IEngineeringServiceProvider");
            if (!provider.IsInstanceOfType(value)) return null;
            return provider.GetMethods().Single(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0).MakeGenericMethod(Type(name)).Invoke(value, null);
        }
    }
}
