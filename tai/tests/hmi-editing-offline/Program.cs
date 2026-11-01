using System;
using System.IO;
using System.Text;
using System.Text.Json;
using TiaMcpServer.Siemens;

internal static class Program
{
    private enum Mode { Off, On }
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++;
    }
    private static void Reject(Action action, string name)
    {
        try { action(); } catch { passed++; return; }
        throw new Exception("FAILED (accepted): " + name);
    }
    private static JsonElement Json(string text) { using (var doc = JsonDocument.Parse(text)) return doc.RootElement.Clone(); }
    private static byte[] Xml(string text) => Encoding.UTF8.GetBytes(text);
    private static void Main()
    {
        Check((int)HmiEditing.ConvertValue(Json("42"), typeof(int)) == 42, "typed integer");
        Check((bool)HmiEditing.ConvertValue(Json("true"), typeof(bool)), "typed bool");
        Check((string)HmiEditing.ConvertValue(Json("\"中文\""), typeof(string)) == "中文", "unicode string");
        Check((Mode)HmiEditing.ConvertValue(Json("\"On\""), typeof(Mode)) == Mode.On, "exact enum");
        Reject(() => HmiEditing.ConvertValue(Json("\"on\""), typeof(Mode)), "enum case");
        Reject(() => HmiEditing.ConvertValue(Json("1"), typeof(Mode)), "numeric enum");
        Reject(() => HmiEditing.ConvertValue(Json("null"), typeof(string)), "unknown null type");
        Reject(() => HmiEditing.ConvertValue(Json("256"), typeof(byte)), "overflow");
        Reject(() => HmiEditing.ConvertValue(Json("1.5"), typeof(int)), "lossy integer");
        Reject(() => HmiEditing.ConvertValue(Json("\"42\""), typeof(int)), "string coercion");
        Reject(() => HmiEditing.ConvertValue(Json("{}"), typeof(object)), "complex type");
        const string start = "<Document><Engineering version='V21'/><DocumentInfo/><Hmi.Screen.Screen ID='0'><AttributeList><Name>画面 1</Name></AttributeList>";
        const string end = "</Hmi.Screen.Screen></Document>";
        var valid = Xml(start + "<ObjectList><Hmi.Screen.Button ID='1'/></ObjectList>" + end);
        Check(HmiEditing.ValidateImport(valid, "Hmi.Screen.Screen", "画面 1").Name.LocalName == "Hmi.Screen.Screen", "single existing screen");
        Reject(() => HmiEditing.ValidateImport(valid, "Hmi.Screen.ScreenTemplate", "画面 1"), "wrong object kind");
        Reject(() => HmiEditing.ValidateImport(valid, "Hmi.Screen.Screen", "画面 2"), "rename");
        Reject(() => HmiEditing.ValidateImport(Xml("<Document><Hmi.Screen.Screen/><Hmi.Screen.Screen/></Document>"), "Hmi.Screen.Screen", ""), "multiple objects");
        Reject(() => HmiEditing.ValidateImport(Xml("<Document><Hmi.Screen.Screen/><Hmi.Tag.Tag/></Document>"), "Hmi.Screen.Screen", ""), "additional unrelated object");
        Reject(() => HmiEditing.ValidateImport(Xml("<!DOCTYPE Document [<!ENTITY x SYSTEM 'file:///C:/secret'>]><Document>&x;</Document>"), "Hmi.Screen.Screen", ""), "DTD and external entity");
        Reject(() => HmiEditing.ValidateImport(Xml("<Document xmlns='unexpected'><Hmi.Screen.Screen/></Document>"), "Hmi.Screen.Screen", ""), "unknown namespace");
        Reject(() => HmiEditing.ValidateImport(Xml("<Document>"), "Hmi.Screen.Screen", ""), "malformed XML");
        Reject(() => HmiEditing.ValidateImport(Xml(start.Replace("<Name>画面 1</Name>", "<Name>画面 1</Name><Name>Other</Name>") + end), "Hmi.Screen.Screen", "画面 1"), "duplicate identity Name");
        Reject(() => HmiEditing.ValidateImport(Xml(start + "<AttributeList><Name>Other</Name></AttributeList>" + end), "Hmi.Screen.Screen", "画面 1"), "duplicate AttributeList");
        Check(HmiEditing.ValidateImport(Xml("<Document><Hmi.Screen.ScreenTemplate><AttributeList><Name>T</Name></AttributeList></Hmi.Screen.ScreenTemplate></Document>"), "Hmi.Screen.ScreenTemplate", "T").Name.LocalName == "Hmi.Screen.ScreenTemplate", "template identity");
        Check(HmiEditing.ValidateImport(Xml("<Document><Hmi.Screen.ScreenGlobalElements><AttributeList/></Hmi.Screen.ScreenGlobalElements></Document>"), "Hmi.Screen.ScreenGlobalElements", "").Name.LocalName == "Hmi.Screen.ScreenGlobalElements", "global elements identity");
        Check(HmiEditing.Hash(Xml("abc")) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "exact byte SHA256");
        Reject(() => HmiEditing.LocalFile("relative.xml", false), "relative output");
        Reject(() => HmiEditing.LocalFile(@"\\server\share\file.xml", false), "UNC output");
        Reject(() => HmiEditing.LocalFile(@"C:\file.xml:stream.xml", false), "alternate stream");
        Reject(() => HmiEditing.LocalFile(@"C:\file.txt", false), "non XML output");
        string folder = Path.Combine(Path.GetTempPath(), "hmi-editing-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string file = Path.Combine(folder, "artifact.xml");
            Check(HmiEditing.LocalFile(file, false) == file, "new local output");
            File.WriteAllBytes(file, valid);
            Reject(() => HmiEditing.LocalFile(file, false), "existing artifact protected");
            Check(HmiEditing.LocalFile(file, true) == file, "existing local import");
            Reject(() => HmiEditing.LocalFile(Path.Combine(folder, "missing.xml"), true), "missing import");
            File.Delete(file);
        }
        finally { Directory.Delete(folder); }
        Console.WriteLine("PASS: " + passed + " offline HMI editing checks. No TIA session or project calls.");
    }
}
