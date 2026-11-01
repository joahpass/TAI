using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Units;
using TiaMcpServer.Compatibility;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "v19-plc-tests-" + Guid.NewGuid().ToString("N"));
    private static int passed;
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action, string contains = "") { try { action(); } catch (Exception ex) { Assert(ex.Message.Contains(contains, StringComparison.OrdinalIgnoreCase), ex.ToString()); return; } throw new Exception("Expected exception: " + contains); }
    private static PlcObject Reset()
    {
        V19Tools.Fixture = new PlcSoftware(); var block = new PlcObject { Name = "Main" }; V19Tools.Fixture.BlockGroup.Blocks.Add(block); return block;
    }
    private static void Test(string name, Action body) { Reset(); body(); passed++; Console.WriteLine("PASS " + name); }
    private static string FileOf(string name, string content) { var path = Path.Combine(Root, name); File.WriteAllText(path, content); return path; }
    private static string Output() => Path.Combine(Root, Guid.NewGuid().ToString("N"));
    private static void Main()
    {
        Directory.CreateDirectory(Root);
        Test("delete verified backup and postcondition", () => {
            var result = V19Tools.DeleteBlock("PLC/CPU", "Program blocks/Main", Root);
            Assert(result.Succeeded && result.complete && result.readable && result.mutationAttempted && result.postconditionVerified, result.Message);
            Assert(V19Tools.Fixture.BlockGroup.Blocks.Count == 0 && result.backups[0].files[0].sha256.Length == 64 && result.backups[0].files[0].bytes > 0, "backup");
            Assert(JsonDocument.Parse(File.ReadAllText(Path.Combine(result.backupPath, "manifest.json"))).RootElement.GetProperty("Succeeded").GetBoolean(), "final manifest");
        });
        Test("rename nested group", () => {
            var group = new Group { Name = "Nested" }; group.Blocks.Add(new PlcObject { Name = "Old" }); V19Tools.Fixture.BlockGroup.Groups.Add(group);
            var r = V19Tools.RenameBlock("PLC/CPU", "Nested/Old", "New", Root); Assert(r.Succeeded && r.newBlockPath == "Nested/New", r.Message);
        });
        foreach (var path in new[] { "*", "../Main", "Main?", "Main//x", "Main\\x", " Main" })
            Test("reject literal " + path, () => { var r = V19Tools.DeleteBlock("PLC/CPU", path, Root); Assert(!r.Succeeded && !r.mutationAttempted, "must reject"); });
        Test("invalid software fails without mutation", () => { var r = V19Tools.DeleteBlock("other", "Main", Root); Assert(!r.Succeeded && !r.mutationAttempted, "software"); });
        Test("rename conflict across units", () => {
            var provider = new PlcUnitProvider(); var unit = new Unit(); unit.BlockGroup.Blocks.Add(new PlcObject { Name = "Taken" }); provider.UnitGroup.Units.Add(unit); V19Tools.Fixture.Services[typeof(PlcUnitProvider)] = provider;
            var r = V19Tools.RenameBlock("PLC/CPU", "Main", "taken", Root); Assert(!r.Succeeded && !r.mutationAttempted, "conflict");
        });
        foreach (var language in new[] { "F_SCL", "FCP", "FLD", "CPU_DB", "SDB" }) Test("protected language " + language, () => { V19Tools.Fixture.BlockGroup.Blocks[0].ProgrammingLanguage = language; var r = V19Tools.DeleteBlock("PLC/CPU", "Main", Root); Assert(!r.Succeeded && !r.mutationAttempted, "protection"); });
        Test("safety OB", () => { V19Tools.Fixture.BlockGroup.Blocks[0].SecondaryType = "SafetyMain"; Assert(!V19Tools.DeleteBlock("PLC/CPU", "Main", Root).mutationAttempted, "safety"); });
        Test("know how protected", () => { V19Tools.Fixture.BlockGroup.Blocks[0].IsKnowHowProtected = true; Assert(!V19Tools.DeleteBlock("PLC/CPU", "Main", Root).mutationAttempted, "knowhow"); });
        Test("write protection defined", () => { V19Tools.Fixture.BlockGroup.Blocks[0].Services[typeof(PlcBlockWriteProtectionProvider)] = new PlcBlockWriteProtectionProvider { IsDefined = true }; Assert(!V19Tools.DeleteBlock("PLC/CPU", "Main", Root).mutationAttempted, "write protection"); });
        foreach (var mode in new[] { "empty", "missing", "malformed", "throw" }) Test("backup failure " + mode, () => { V19Tools.Fixture.BlockGroup.Blocks[0].ExportMode = mode; var r = V19Tools.DeleteBlock("PLC/CPU", "Main", Root); Assert(!r.Succeeded && !r.complete && !r.mutationAttempted && r.backups[0].files.Count == 0, r.Message); });
        Test("inconsistent backup no fake document conversion", () => { V19Tools.Fixture.BlockGroup.Blocks[0].IsConsistent = false; var r = V19Tools.BackupSoftwareForEdit("PLC/CPU", Root); Assert(!r.complete && !r.Succeeded && r.blockers.Any(s => s.Contains("V19")), r.Message); });
        Test("changed timestamp prevents delete", () => { var b = V19Tools.Fixture.BlockGroup.Blocks[0]; b.OnExport = () => b.ModifiedDate = b.ModifiedDate.AddSeconds(1); var r = V19Tools.DeleteBlock("PLC/CPU", "Main", Root); Assert(!r.Succeeded && !r.mutationAttempted, r.Message); });
        Test("postcondition detects SDK no-op", () => { V19Tools.Fixture.BlockGroup.Blocks[0].DeleteNoop = true; var r = V19Tools.DeleteBlock("PLC/CPU", "Main", Root); Assert(!r.Succeeded && r.mutationAttempted && !r.postconditionVerified, r.Message); });
        Test("backup covers system technology units safety", () => {
            var system = new Group { Name = "System" }; system.Blocks.Add(new PlcObject { Name = "Sys" }); V19Tools.Fixture.BlockGroup.SystemBlockGroups.Add(system);
            V19Tools.Fixture.TechnologicalObjectGroup.TechnologicalObjects.Add(new PlcObject { Name = "Axis" });
            var provider = new PlcUnitProvider(); var unit = new Unit(); unit.BlockGroup.Blocks.Add(new PlcObject { Name = "UnitBlock" }); provider.UnitGroup.Units.Add(unit);
            var safety = new Unit { Name = "Safe" }; safety.BlockGroup.Blocks.Add(new PlcObject { Name = "SafetyBlock" }); provider.UnitGroup.SafetyUnits.Add(safety); V19Tools.Fixture.Services[typeof(PlcUnitProvider)] = provider;
            var r = V19Tools.BackupSoftwareForEdit("PLC/CPU", Root); Assert(r.Succeeded && r.complete && r.totalBlocks == 5 && r.backups.Count == 5, r.Message);
            Assert(!V19Tools.DeleteBlock("PLC/CPU", "@system/System/Sys", Root).mutationAttempted, "system delete");
            Assert(!V19Tools.DeleteBlock("PLC/CPU", "@technology/Axis", Root).mutationAttempted, "technology delete");
            Assert(!V19Tools.DeleteBlock("PLC/CPU", "@safetyUnits/Safe/SafetyBlock", Root).mutationAttempted, "safety unit delete");
        });
        Test("changing inventory makes backup incomplete", () => { V19Tools.Fixture.BlockGroup.Blocks[0].OnExport = () => V19Tools.Fixture.BlockGroup.Blocks.Add(new PlcObject { Name = "Added" }); var r = V19Tools.BackupSoftwareForEdit("PLC/CPU", Root); Assert(!r.complete && !r.Succeeded, r.Message); });
        Test("duplicate inventory fails closed", () => { V19Tools.Fixture.BlockGroup.Blocks.Add(new PlcObject { Name = "Main" }); var r = V19Tools.BackupSoftwareForEdit("PLC/CPU", Root); Assert(!r.complete && !r.Succeeded, r.Message); });
        Test("relative backup rejected", () => { var r = V19Tools.DeleteBlock("PLC/CPU", "Main", "relative"); Assert(!r.mutationAttempted && !r.Succeeded, r.Message); });
        Test("XML block and type imports execute public compositions", () => {
            var block = FileOf("block.xml", "<Document><SW.Blocks.FC /></Document>"); var type = FileOf("type.xml", "<Document><SW.Types.PlcStruct /></Document>");
            Assert(Json(V19Tools.ImportBlock("PLC/CPU", "", block)).GetProperty("Meta").GetProperty("success").GetBoolean(), "block import");
            Assert(Json(V19Tools.ImportType("PLC/CPU", "PLC data types", type)).GetProperty("importedCount").GetInt32() == 1, "type import");
            Assert(V19Tools.Fixture.BlockGroup.Blocks.ImportCalls == 1 && V19Tools.Fixture.TypeGroup.Types.ImportCalls == 1, "actual public methods");
        });
        Test("imports reject wrong format before SDK", () => {
            foreach (var f in new[] { FileOf("wrong.s7dcl", "DATA_BLOCK X"), FileOf("wrong.scl", "FUNCTION X"), FileOf("bad.xml", "<x/>"), FileOf("empty.xml", ""), FileOf("broken.xml", "<Document>"), FileOf("dtd.xml", "<!DOCTYPE Document [<!ENTITY a SYSTEM 'file:///x'>]><Document><SW.Blocks.FC/></Document>") }) Throws(() => V19Tools.ImportBlock("PLC/CPU", "", f));
            Throws(() => V19Tools.ImportType("PLC/CPU", "", FileOf("not-type.xml", "<Document><SW.Blocks.FC/></Document>")));
            Assert(V19Tools.Fixture.BlockGroup.Blocks.ImportCalls == 0, "no SDK calls");
        });
        Test("missing group does not import root", () => { Throws(() => V19Tools.ImportBlock("PLC/CPU", "missing", FileOf("group.xml", "<Document><SW.Blocks.FC/></Document>")), "group"); Assert(V19Tools.Fixture.BlockGroup.Blocks.ImportCalls == 0, "no root fallback"); });
        Test("empty import and SDK errors propagate", () => {
            var file = FileOf("import.xml", "<Document><SW.Blocks.FC/></Document>"); V19Tools.Fixture.BlockGroup.Blocks.ImportEmpty = true; Throws(() => V19Tools.ImportBlock("PLC/CPU", "", file), "no imported");
            V19Tools.Fixture.BlockGroup.Blocks.ImportThrow = true; Throws(() => V19Tools.ImportBlock("PLC/CPU", "", file), "fake import failure");
        });
        Test("type info and regex nested contract", () => {
            var group = new Group { Name = "Nested" }; group.Types.Add(new PlcObject { Name = "Packet" }); V19Tools.Fixture.TypeGroup.Groups.Add(group);
            var info = Json(V19Tools.GetTypeInfo("PLC/CPU", "PLC data types/Nested/Packet")); Assert(info.GetProperty("Name").GetString() == "Packet" && info.GetProperty("Attributes").GetArrayLength() == 1, "type info");
            Assert(Json(V19Tools.GetTypes("PLC/CPU", "^Packet$")).GetProperty("Items").GetArrayLength() == 1, "type regex");
            Throws(() => V19Tools.GetTypes("PLC/CPU", "[")); Throws(() => V19Tools.GetTypeInfo("PLC/CPU", "Missing"));
        });
        Test("type exports retain metadata and structure", () => {
            V19Tools.Fixture.TypeGroup.Types.Add(new PlcObject { Name = "Packet" }); var output = Output(); var result = Json(V19Tools.ExportType("PLC/CPU", output, "Packet", true)); Assert(File.Exists(result.GetProperty("ExportPath").GetString()), "type file");
            V19Tools.Fixture.TypeGroup.Types.Add(new PlcObject { Name = "Inconsistent", IsConsistent = false });
            var batch = Json(V19Tools.ExportTypes("PLC/CPU", output)); Assert(batch.GetProperty("Items").GetArrayLength() == 1 && batch.GetProperty("Inconsistent").GetArrayLength() == 1 && batch.GetProperty("Meta").GetProperty("totalTypes").GetInt32() == 2 && !batch.GetProperty("complete").GetBoolean(), "type batch");
        });
        Test("batch partial errors do not report success", () => {
            V19Tools.Fixture.BlockGroup.Blocks.Add(new PlcObject { Name = "Broken", ExportMode = "throw" }); V19Tools.Fixture.BlockGroup.Blocks.Add(new PlcObject { Name = "Inconsistent", IsConsistent = false });
            var result = Json(V19Tools.ExportBlocks("PLC/CPU", Output())); Assert(!result.GetProperty("Succeeded").GetBoolean() && result.GetProperty("Items").GetArrayLength() == 1 && result.GetProperty("Failures").GetArrayLength() == 1 && result.GetProperty("Inconsistent").GetArrayLength() == 1, "batch partial");
        });
        Test("export failure preserves existing file", () => { var output = Output(); Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "Main.xml"), "original"); V19Tools.Fixture.BlockGroup.Blocks[0].ExportMode = "malformed"; V19Tools.ExportBlocks("PLC/CPU", output); Assert(File.ReadAllText(Path.Combine(output, "Main.xml")) == "original", "preserved"); });
        Test("flat collisions and path traversal rejected", () => { var group = new Group { Name = "Nested" }; group.Blocks.Add(new PlcObject { Name = "Main" }); V19Tools.Fixture.BlockGroup.Groups.Add(group); Throws(() => V19Tools.ExportBlocks("PLC/CPU", Output()), "collide"); Assert(Json(V19Tools.ExportBlocks("PLC/CPU", Output(), "", true)).GetProperty("Items").GetArrayLength() == 2, "preserve path"); V19Tools.Fixture.BlockGroup.Blocks[0].Name = "../Escape"; Throws(() => V19Tools.ExportBlocks("PLC/CPU", Output())); });
        Test("all document tools explicit unsupported", () => {
            var results = new[] { V19Tools.ExportAsDocuments("", "", ""), V19Tools.ExportBlocksAsDocuments("", ""), V19Tools.ImportFromDocuments("", "", "", ""), V19Tools.ImportBlocksFromDocuments("", "", "") };
            foreach (var result in results) Assert(!Json(result).GetProperty("Succeeded").GetBoolean() && !Json(result).GetProperty("Supported").GetBoolean(), "unsupported");
        });
        Test("external source creates and generates actual public API", () => { var file = FileOf("source.scl", "FUNCTION Test : Void\nBEGIN\nEND_FUNCTION"); var result = Json(V19Tools.ImportExternalSource("PLC/CPU", file)); Assert(result.GetProperty("Succeeded").GetBoolean() && result.GetProperty("Items").GetArrayLength() == 1 && V19Tools.Fixture.ExternalSourceGroup.ExternalSources.Count == 1, "source"); Throws(() => V19Tools.ImportExternalSource("PLC/CPU", file), "already exists"); });
        Test("external generation failure reports possible partial writes", () => { V19Tools.Fixture.ExternalSourceGroup.ExternalSources.GenerateThrow = true; Throws(() => V19Tools.ImportExternalSource("PLC/CPU", FileOf("failed.scl", "FUNCTION X : Void\nEND_FUNCTION")), "partially modified"); Assert(V19Tools.Fixture.ExternalSourceGroup.ExternalSources.Count == 1, "retain source for diagnostics"); });
        Test("external source rejects disguised XML", () => { Throws(() => V19Tools.ImportExternalSource("PLC/CPU", FileOf("disguised.scl", "<Document/>")), "standard SCL"); Assert(V19Tools.Fixture.ExternalSourceGroup.ExternalSources.Count == 0, "no mutation"); });
        Test("attribute statuses preserve partial and readable contract", () => {
            var type = new PlcObject { Name = "Packet", AttributeAccess = "Write", AttributeThrow = true }; V19Tools.Fixture.TypeGroup.Types.Add(type);
            var info = Json(V19Tools.GetTypeInfo("PLC/CPU", "Packet")); Assert(info.GetProperty("Attributes")[0].GetProperty("Status").GetString() == "NotReadable" && !info.GetProperty("Meta").GetProperty("success").GetBoolean(), "write-only attribute");
            type.AttributeAccess = "ReadWrite"; info = Json(V19Tools.GetTypeInfo("PLC/CPU", "Packet")); Assert(info.GetProperty("Attributes")[0].GetProperty("Status").GetString() == "ReadFailed", "read failure");
            type.AttributeThrow = false; type.AttributeOverride = type; var list = Json(V19Tools.GetTypes("PLC/CPU", "packet")); Assert(list.GetProperty("Items").GetArrayLength() == 1 && list.GetProperty("Items")[0].GetProperty("Attributes")[0].GetProperty("Status").GetString() == "ProjectionFailed" && list.GetProperty("Meta").GetProperty("partial").GetBoolean(), "raw SDK projection");
        });
        Test("external generation empty result fails", () => { V19Tools.Fixture.ExternalSourceGroup.ExternalSources.GenerateEmpty = true; Throws(() => V19Tools.ImportExternalSource("PLC/CPU", FileOf("empty-generated.scl", "FUNCTION X : Void\nEND_FUNCTION")), "no blocks"); });
        Console.WriteLine("V19 PLC offline tests passed: " + passed + "; artifacts: " + Root);
    }
}
