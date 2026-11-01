using System;
using System.IO;
using System.Text;
using System.Xml;
using TiaMcpServer.Siemens;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        checks++;
    }
    private static void Reject<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T) { checks++; return; }
        throw new Exception("FAILED (accepted): " + label);
    }
    public static int Main()
    {
        string directory = Path.Combine(Path.GetTempPath(), "classic-screen-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = new FileInfo(Path.Combine(directory, "fixture.xml"));
        try
        {
            // Synthetic schema: verifies preservation, never claims Siemens fixture coverage.
            const string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Screen Name=\"画面\"><Object Type=\"IO\" Left=\"10\" Top=\"20\" Tag=\"DB.X\" Connection=\"PLC\">😀 &amp; value</Object></Screen>";
            File.WriteAllText(file.FullName, xml, new UTF8Encoding(true));
            var whole = ClassicScreenContent.ReadExport(file, 0, 20000, null);
            Check(whole.success && whole.complete && whole.endOfDocument && whole.xml == xml, "exact UTF8 BOM XML");
            Check(whole.totalBytes == new FileInfo(file.FullName).Length && whole.totalChars == xml.Length && whole.sha256.Length == 64, "metadata");
            var assembled = new StringBuilder();
            int offset = 0;
            do
            {
                var page = ClassicScreenContent.ReadExport(file, offset, 7, offset == 0 ? null : whole.sha256);
                Check(page.returnedChars <= 7 && !page.complete, "bounded partial page");
                assembled.Append(page.xml);
                if (!page.nextOffset.HasValue) { Check(page.endOfDocument, "last page"); break; }
                offset = page.nextOffset.Value;
            } while (true);
            Check(assembled.ToString() == xml, "all pages reconstruct exact XML");
            var eof = ClassicScreenContent.ReadExport(file, xml.Length, 10, whole.sha256);
            Check(eof.xml == "" && eof.endOfDocument && !eof.complete, "EOF page");
            Reject<ArgumentException>(() => ClassicScreenContent.ReadExport(file, 1, 20, null), "continuation hash mandatory");
            Reject<ArgumentOutOfRangeException>(() => ClassicScreenContent.ReadExport(file, 0, 20001, null), "page upper limit");
            Reject<ArgumentOutOfRangeException>(() => ClassicScreenContent.ReadExport(file, -1, 20, null), "negative offset");
            Reject<ArgumentOutOfRangeException>(() => ClassicScreenContent.ReadExport(file, xml.Length + 1, 20, whole.sha256), "past EOF");
            Reject<ArgumentException>(() => ClassicScreenContent.ReadExport(file, 0, 20, "bad"), "hash syntax");
            int emoji = xml.IndexOf("😀", StringComparison.Ordinal);
            Reject<ArgumentException>(() => ClassicScreenContent.ReadExport(file, emoji + 1, 20, whole.sha256), "split surrogate offset");
            Reject<ArgumentException>(() => ClassicScreenContent.ReadExport(file, emoji, 1, whole.sha256), "no nonprogress page");
            File.AppendAllText(file.FullName, " ");
            Reject<InvalidOperationException>(() => ClassicScreenContent.ReadExport(file, 1, 20, whole.sha256), "changed snapshot");
            File.WriteAllText(file.FullName, "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///not-read'>]><x>&e;</x>");
            Reject<XmlException>(() => ClassicScreenContent.ReadExport(file, 0, 20, null), "DTD and external entity");
            File.WriteAllText(file.FullName, "<x>valid prefix</x><broken");
            Reject<XmlException>(() => ClassicScreenContent.ReadExport(file, 0, 2, null), "invalid tail outside page");
            foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
            {
                string fixture = "<?xml version=\"1.0\" encoding=\"" + encoding.WebName + "\"?><x>画面😀</x>";
                File.WriteAllText(file.FullName, fixture, encoding);
                Check(ClassicScreenContent.ReadExport(file, 0, 20000, null).xml == fixture, "encoding " + encoding.WebName);
            }
            File.WriteAllBytes(file.FullName, new byte[0]);
            Reject<InvalidDataException>(() => ClassicScreenContent.ReadExport(file, 0, 20, null), "empty export");
            using (var stream = File.Create(file.FullName)) stream.SetLength(ClassicScreenContent.MaximumExportBytes + 1L);
            Reject<InvalidDataException>(() => ClassicScreenContent.ReadExport(file, 0, 20, null), "oversized export");
            Console.WriteLine("PASS: " + checks + " offline checks; no TIA objects created or Export invoked.");
            return 0;
        }
        finally { File.Delete(file.FullName); Directory.Delete(directory); }
    }
}
