using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcpServer.ModelContextProtocol;
using ProjectedAttribute = TiaMcpServer.ModelContextProtocol.Attribute;

internal static class Program
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }
    private static ProjectedAttribute Read(object? value) => AttributeProjection.Read("Test", "Read", true, () => value);
    private static void Main()
    {
        bool originalFailed = false;
        try { JsonSerializer.Serialize(new { Value = typeof(Program) }); }
        catch (NotSupportedException) { originalFailed = true; }
        Check(originalFailed, "Raw unsupported object reproduces serializer failure offline");
        var unsupported = Read(typeof(Program));
        Check(!unsupported.Succeeded && unsupported.Status == "ProjectionFailed" && unsupported.Value == null && unsupported.Error!.Contains("System.RuntimeType"), "Unsupported object isolated with concrete type and diagnostic");
        JsonSerializer.Serialize(unsupported);
        Check(Read(null).Succeeded && Read(null).Value == null, "Legitimate null remains successful");
        Check((int)Read(42).Value! == 42 && (bool)Read(true).Value!, "Primitives preserve JSON types");
        Check((string)Read(DayOfWeek.Monday).Value! == "Monday", "Enum projects as readable name");
        Check((string)Read(CultureInfo.GetCultureInfo("de-DE")).Value! == "de-DE", "Culture projects without SDK-style property traversal");
        Check((string)Read(new DirectoryInfo(".")).Value! == new DirectoryInfo(".").FullName, "Filesystem metadata projects as path without recursive properties");
        var date = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Check((string)Read(date).Value! == date.ToString("O", CultureInfo.InvariantCulture), "Timestamp projects with round-trip precision");
        var nested = Read(new Dictionary<string, object?> { ["items"] = new object?[] { 1, null, DayOfWeek.Friday } });
        Check(nested.Succeeded && JsonSerializer.Serialize(nested.Value).Contains("Friday"), "Nested dictionary and list recursively project");
        var readError = AttributeProjection.Read("Locked", "Read", true, () => throw new InvalidOperationException("Read denied", new IOException("Underlying detail")));
        Check(readError.Status == "ReadFailed" && readError.Error!.Contains("Underlying detail") && readError.Error.Contains("System.IO.IOException"), "Read failure retains inner exception type and message");
        bool invoked = false;
        var writeOnly = AttributeProjection.Read("WriteOnly", "Write", false, () => { invoked = true; return 1; });
        Check(!invoked && writeOnly.Status == "NotReadable", "Write-only attributes never call getter");
        var cycle = new ArrayList(); cycle.Add(cycle);
        Check(Read(cycle).Status == "ProjectionFailed", "Cycles terminate with explicit failure");
        Check(Read(new int[257]).Status == "ProjectionFailed", "Large collections fail explicitly instead of truncating");
        Check(Read(double.NaN).Status == "ProjectionFailed" && Read(double.PositiveInfinity).Status == "ProjectionFailed", "Non-finite numbers cannot escape into serializer");
        Check(Read(new Hashtable { [1] = "bad key" }).Status == "ProjectionFailed", "Non-string dictionary keys fail explicitly");
        var poison = new Poison();
        Check(Read(poison).Status == "ProjectionFailed" && !poison.Touched, "Unknown object properties and ToString are never invoked");
        var attributes = new[] { Read(1), readError, unsupported, writeOnly, Read(null) };
        var device = new ResponseDeviceInfo { Name = "CPU", Attributes = attributes, Meta = Success() };
        var item = new ResponseDeviceItemInfo { Attributes = attributes, Meta = Success() };
        var projects = new ResponseGetProjects { Items = new[] { new ResponseProjectInfo { Attributes = attributes } }, Meta = Success() };
        var devices = new ResponseDevices { Items = new[] { device }, Meta = Success() };
        foreach (var response in new ResponseMessage[] { device, item, projects, devices })
        {
            Check(response.Meta!["success"]!.GetValue<bool>() == false && response.Meta["partial"]!.GetValue<bool>() && response.Meta["attributeFailureCount"]!.GetValue<int>() == 3, response.GetType().Name + " cannot report false success");
            JsonSerializer.Serialize(response, response.GetType());
        }
        var healthy = new ResponseDeviceInfo { Attributes = new[] { Read(1), Read(null) }, Meta = Success() };
        Check(healthy.Meta!["success"]!.GetValue<bool>() && healthy.Meta["attributesComplete"]!.GetValue<bool>(), "Fully readable response remains complete");
        SiemensProjectionTests.Run(Check);
        Console.WriteLine($"{passed} offline assertions passed; no TIA assemblies, connection, or MCP host used.");
    }
    private static JsonObject Success() => new JsonObject { ["success"] = true };
    private sealed class Poison
    {
        public bool Touched;
        public object Value { get { Touched = true; throw new Exception("getter called"); } }
        public override string ToString() { Touched = true; throw new Exception("ToString called"); }
    }
}
