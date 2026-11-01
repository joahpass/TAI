using System;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.Hmi.Screen;
using TiaMcpServer.Siemens;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAILED: " + label);
        checks++;
    }
    private static void Reject<T>(Action action, string diagnostic) where T : Exception
    {
        try { action(); }
        catch (T ex) { Check(ex.Message.Contains(diagnostic), diagnostic); return; }
        throw new Exception("FAILED: accepted " + diagnostic);
    }
    public static int Main()
    {
        var paths = new[] { "screens/画面", "screens/画面/根画面", "templates/模板/模板_1", "special/globalElements", "screens/画面/A.[1]", "screens/画面/ spaced " };
        foreach (var path in paths)
            Check(HmiContentPathResolution.Resolve(paths, path) == path, "exact canonical " + path);
        Check(HmiContentPathResolution.Resolve(paths, "画面/根画面") == "screens/画面/根画面", "reported missing category");
        Check(HmiContentPathResolution.Resolve(paths, "模板/模板_1") == "templates/模板/模板_1", "template alias");
        Check(HmiContentPathResolution.Resolve(paths, "globalElements") == "special/globalElements", "global elements alias");
        Check(HmiContentPathResolution.Resolve(paths, "画面/A.[1]") == "screens/画面/A.[1]", "literal regex punctuation");
        Check(HmiContentPathResolution.Resolve(paths, "画面/ spaced ") == "screens/画面/ spaced ", "literal whitespace");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(paths, "根画面"), "screens/画面/根画面");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(paths, "special/globalelements"), "HMI path was not found");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(paths, "画面/A.*"), "HMI path was not found");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(paths, "画面/spaced"), "HMI path was not found");
        var ambiguous = new[] { "screens/shared/name", "templates/shared/name" };
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(ambiguous, "shared/name"), "Ambiguous category-omitted");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(ambiguous, "shared/name"), "templates/shared/name");
        Check(HmiContentPathResolution.Resolve(ambiguous, ambiguous[0]) == ambiguous[0], "canonical wins");
        var collision = new[] { "screens/a", "templates/screens/a" };
        Check(HmiContentPathResolution.Resolve(collision, "screens/a") == "screens/a", "exact wins over other alias");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(new[] { paths[1], paths[1] }, paths[1]), "Ambiguous canonical");
        foreach (var invalid in new[] { "", "screens//x", "screens/../x", "screens/./x", "screens/x/", "screens/\nx" })
            Reject<ArgumentException>(() => HmiContentPathResolution.Resolve(paths, invalid), "slash-separated");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(new string[0], "none"), "(none)");
        Reject<InvalidOperationException>(() => HmiContentPathResolution.Resolve(Enumerable.Range(0, 21).Select(i => "screens/" + i), "none"), "pagination for all paths");

        // Verify the installed PUBLIC SDK, without constructing engineering objects or invoking export.
        foreach (var type in new[] { typeof(Screen), typeof(ScreenTemplate), typeof(ScreenGlobalElements), typeof(ScreenPopup), typeof(ScreenOverview), typeof(ScreenSlidein) })
        {
            var method = type.GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
            Check(method != null && method.IsPublic && method.ReturnType == typeof(void), "public V21 Export: " + type.Name);
            Check(typeof(IEngineeringObject).IsAssignableFrom(type), "engineering object: " + type.Name);
        }
        Check(!ClassicScreenContent.Supports(null!), "unsupported object capability");

        // Exercise the same bytes/paging implementation used for all six export kinds.
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><Screen>画面😀 &amp; binding</Screen>";
        byte[] bytes = new UTF8Encoding(false).GetBytes(xml);
        var first = ClassicScreenContent.ReadBytes(bytes, 0, 9, null);
        var assembled = new StringBuilder(first.xml);
        var page = first;
        while (page.nextOffset.HasValue)
        {
            page = ClassicScreenContent.ReadBytes(bytes, page.nextOffset.Value, 9, first.sha256);
            assembled.Append(page.xml);
            Check(page.sha256 == first.sha256 && page.returnedChars <= 9, "bounded same-hash continuation");
        }
        Check(assembled.ToString() == xml && page.endOfDocument && !page.complete, "exact page reconstruction");
        Check(ClassicScreenContent.ReadBytes(bytes, 0, 20000, null).complete, "whole document");
        Reject<ArgumentException>(() => ClassicScreenContent.ReadBytes(bytes, 1, 9, null), "Continuation requires");
        Reject<InvalidOperationException>(() => ClassicScreenContent.ReadBytes(Encoding.UTF8.GetBytes(xml + " "), 1, 9, first.sha256), "SHA256 changed");
        int emoji = xml.IndexOf("😀", StringComparison.Ordinal);
        Reject<ArgumentException>(() => ClassicScreenContent.ReadBytes(bytes, emoji + 1, 9, first.sha256), "surrogate pair");
        Reject<ArgumentException>(() => ClassicScreenContent.ReadBytes(bytes, emoji, 1, first.sha256), "too small");
        Reject<System.Xml.XmlException>(() => ClassicScreenContent.ReadBytes(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///not-read'>]><x>&e;</x>"), 0, 9, null), "DTD");
        Console.WriteLine("PASS: " + checks + " offline HMI resolution, public V21 SDK and XML pagination checks. No TIA objects created and no exports invoked.");
        return 0;
    }
}
