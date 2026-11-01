// Offline doubles for the narrow public API consumed by the adapter. Production
// compilation against the installed V21 SDK separately verifies these API calls.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using TiaMcpServer.ModelContextProtocol;

namespace Siemens.Engineering
{
    public enum EngineeringAttributeAccessMode { Read = 1, Write = 2 }
    public sealed class EngineeringAttributeInfo { public string Name = ""; public EngineeringAttributeAccessMode AccessMode = EngineeringAttributeAccessMode.Read; }
    public interface IEngineeringObject { object? GetAttribute(string name); IEnumerable<EngineeringAttributeInfo> GetAttributeInfos(); }
    public sealed class Language { public CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture; }
    public sealed class MultilingualText { public List<MultilingualTextItem> Items { get; } = new List<MultilingualTextItem>(); }
    public sealed class MultilingualTextItem
    {
        public Language Language { get; set; } = new Language();
        public string? Text { get; set; }
    }
}
namespace Siemens.Engineering.HW
{
    public sealed class DeviceItemAssociation : ArrayList { }
}
namespace Siemens.Engineering.Security
{
    public sealed class Certificate { public uint Id { get; set; } public string SubjectCommonName { get; set; } = ""; public DateTime ValidUntil { get; set; } public bool HasPrivateKey { get; set; } }
}
namespace Siemens.Engineering.HW.CustomDataTypes
{
    public sealed class TableData { public List<IEngineeringObject> Rows { get; } = new List<IEngineeringObject>(); }
}
internal static class SiemensProjectionTests
{
    private sealed class TableRow : Siemens.Engineering.IEngineeringObject
    {
        public bool Readable = true;
        public object? GetAttribute(string name) => name == "Language" ? "en-US" : throw new Exception("Unexpected cell");
        public IEnumerable<Siemens.Engineering.EngineeringAttributeInfo> GetAttributeInfos() => new[] { new Siemens.Engineering.EngineeringAttributeInfo { Name = "Language", AccessMode = Readable ? Siemens.Engineering.EngineeringAttributeAccessMode.Read : Siemens.Engineering.EngineeringAttributeAccessMode.Write } };
    }
    private sealed class Reference : Siemens.Engineering.IEngineeringObject
    {
        public object? Name = "CPU 1511";
        public bool Fail;
        public int Reads;
        public object? GetAttribute(string name)
        {
            if (name != "Name") throw new Exception("Unexpected graph traversal");
            Reads++;
            if (Fail) throw new InvalidOperationException("Name denied");
            return Name;
        }
        public IEnumerable<Siemens.Engineering.EngineeringAttributeInfo> GetAttributeInfos() => throw new Exception("Reference attributes enumerated");
        public object Parent => throw new Exception("Parent traversed");
        public override string ToString() => throw new Exception("ToString invoked");
    }
    private static TiaMcpServer.ModelContextProtocol.Attribute Read(object value) =>
        AttributeProjection.Read("SDK", "Read", true, () => value, SiemensAttributeProjection.TryConvert);
    public static void Run(Action<bool, string> check)
    {
        var certificate = Read(new Siemens.Engineering.Security.Certificate { Id = 42, SubjectCommonName = "PLC", ValidUntil = new DateTime(2030, 1, 1), HasPrivateKey = true });
        check(certificate.Succeeded && ((Dictionary<string, object?>)certificate.Value!)["SubjectCommonName"] as string == "PLC", "Certificate metadata is JSON-safe without key material or Name reads");
        var table = new Siemens.Engineering.HW.CustomDataTypes.TableData();
        var row = new TableRow(); table.Rows.Add(row);
        var tableResult = Read(table);
        check(tableResult.Succeeded && JsonSerializer.Serialize(tableResult).Contains("en-US"), "Table rows use readable explicit attributes");
        row.Readable = false;
        check(Read(table).Status == "ProjectionFailed", "Unreadable table cells are not silently dropped");
        table.Rows.Clear();
        for (int i = 0; i < 257; i++) table.Rows.Add(new TableRow());
        check(Read(table).Status == "ProjectionFailed", "Table row count is bounded");
        var text = new Siemens.Engineering.MultilingualText();
        text.Items.Add(new Siemens.Engineering.MultilingualTextItem { Language = new Siemens.Engineering.Language { Culture = CultureInfo.GetCultureInfo("en-US") }, Text = "Main CPU" });
        text.Items.Add(new Siemens.Engineering.MultilingualTextItem { Language = new Siemens.Engineering.Language { Culture = CultureInfo.GetCultureInfo("de-DE") }, Text = "Steuerung" });
        var result = Read(text);
        var translations = (Dictionary<string, object?>)result.Value!;
        check(result.Succeeded && (string)translations["en-US"]! == "Main CPU" && (string)translations["de-DE"]! == "Steuerung", "SDK multilingual text uses exact culture/text pairs");
        check(result.ValueType == text.GetType().FullName, "Raw SDK value type survives conversion");
        check(Read(new Siemens.Engineering.MultilingualText()).Succeeded, "Empty multilingual text succeeds");
        text.Items.Add(text.Items[0]);
        check(Read(text).Status == "ProjectionFailed", "Duplicate cultures fail without silently overwriting");
        text.Items.Clear();
        var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
        for (int i = 0; i < 257; i++) text.Items.Add(new Siemens.Engineering.MultilingualTextItem { Language = new Siemens.Engineering.Language { Culture = cultures[i] } });
        check(Read(text).Status == "ProjectionFailed" && Read(text).Error!.Contains("exceeds 256"), "Oversized multilingual data cannot escape projection");
        var reference = new Reference();
        var association = new Siemens.Engineering.HW.DeviceItemAssociation { reference };
        result = Read(association);
        var references = (List<object?>)result.Value!;
        var entry = (Dictionary<string, object?>)references[0]!;
        check(result.Succeeded && (string)entry["Name"]! == "CPU 1511" && (string)entry["Type"]! == reference.GetType().FullName && reference.Reads == 1, "Association children expose meaningful names/types without graph traversal");
        JsonSerializer.Serialize(result);
        check(Read(new ArrayList { reference }).Succeeded, "Other IList reference collections use the same safe adapter");
        check(Read(reference).Succeeded, "Parent reference projects without traversing its graph");
        reference.Fail = true;
        result = Read(association);
        check(result.Status == "ProjectionFailed" && result.Error!.Contains("Name denied") && result.Value == null, "Reference read failure fails its attribute with original diagnostic");
        reference.Fail = false; reference.Name = null;
        check(Read(reference).Status == "ProjectionFailed", "Nameless references fail explicitly");
        reference.Name = 12;
        check(Read(reference).Status == "ProjectionFailed", "Non-string reference names fail explicitly");
        association.Clear();
        for (int i = 0; i < 257; i++) association.Add(new Reference());
        check(Read(association).Status == "ProjectionFailed", "SDK association size is bounded");
        var unknown = new object();
        result = AttributeProjection.Read("Converted", "Read", true, () => unknown,
            (object value, out object? converted) => { converted = value; return true; });
        check(result.Status == "ProjectionFailed" && result.Error!.Contains("nesting"), "Recursive converter output respects depth bound");
        result = AttributeProjection.Read("Converted", "Read", true, () => unknown,
            (object value, out object? converted) => { converted = new Dictionary<string, object?> { ["unsafe"] = double.NaN }; return ReferenceEquals(value, unknown); });
        check(result.Status == "ProjectionFailed", "Converter output is revalidated before serialization");
        check(Read(new Siemens.Engineering.Language { Culture = CultureInfo.GetCultureInfo("en-US") }).Value as string == "en-US", "SDK language projects as culture name");
    }
}
