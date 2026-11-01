using TiaMcpServer.Siemens;

var root = Path.Combine(Path.GetTempPath(), "tai-document-diagnostics-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
    passed++;
}
void Reject(Action action, string expected, string label)
{
    try { action(); throw new Exception("Expected rejection: " + label); }
    catch (PortalException ex) { Check(ex.Message.Contains(expected, StringComparison.Ordinal), label); }
}
string Fixture(string name, string content)
{
    var path = Path.Combine(root, name);
    File.WriteAllText(path, content);
    return path;
}
try
{
    // Shape taken from real TIA SIMATIC SD exports: blank et => is intentional.
    var sd = Fixture("lad.s7dcl", "{ S7_Optimized := \"TRUE\"; S7_PreferredLanguage := \"LAD\" }\nFUNCTION \"块_1\" : Void\n{ S7_Language := \"LAD\" }\nNETWORK\nRUNG wire#powerrail\n\"IEC_Timer_0_DB\".TON(pt := T#60s, et => )\nEND_RUNG\nEND_NETWORK\nEND_FUNCTION");
    Check(EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "lad").FullName == sd, "LAD SD and absent optional s7res accepted");
    Reject(() => EngineeringDocumentDiagnostics.ValidateXmlImport(sd), "Use ImportFromDocuments", "SD sent to XML API gives actionable route");
    var xml = Fixture("block.xml", "<?xml version=\"1.0\"?><Document><SW.Blocks.FC /></Document>");
    Check(EngineeringDocumentDiagnostics.ValidateXmlImport(xml).FullName == xml, "SIMATIC ML XML accepted");
    var alternateExtension = Fixture("block.export", "<Document />");
    Check(EngineeringDocumentDiagnostics.ValidateXmlImport(alternateExtension).Exists, "valid XML alternate extension remains compatible");
    var renamed = Fixture("renamed.s7dcl", "  <Document />");
    Reject(() => EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "renamed"), "starts with XML markup", "renaming XML cannot convert format");
    var sdAsXml = Fixture("sd.xml", "{ S7_Optimized := \"TRUE\" }\nDATA_BLOCK Test\nEND_DATA_BLOCK");
    Reject(() => EngineeringDocumentDiagnostics.ValidateXmlImport(sdAsXml), "not well-formed", "SD renamed XML rejected by XML parser");
    var malformed = Fixture("broken.xml", "<Document>\n<SW.Blocks.FC></Document>");
    Reject(() => EngineeringDocumentDiagnostics.ValidateXmlImport(malformed), "Line 2", "XML syntax diagnostics retain line information");
    var wrongRoot = Fixture("other.xml", "<Other />");
    Reject(() => EngineeringDocumentDiagnostics.ValidateXmlImport(wrongRoot), "Document root", "non SIMATIC ML root rejected");
    var dtd = Fixture("dtd.xml", "<!DOCTYPE Document [<!ENTITY leak SYSTEM 'file:///never-read'>]><Document>&leak;</Document>");
    Reject(() => EngineeringDocumentDiagnostics.ValidateXmlImport(dtd), "DTD", "XML preflight cannot resolve external entities");
    Reject(() => EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "lad.s7dcl"), "base name", "extension in base-name argument rejected");
    Reject(() => EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "../lad"), "base name", "directory traversal base-name rejected");
    Reject(() => EngineeringDocumentDiagnostics.ValidateDocumentImport(sd, "lad"), "existing directory", "file supplied as document directory rejected");
    Reject(() => EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "missing"), "missing.s7dcl", "missing file reports exact attempted document");
    Fixture("empty.s7dcl", " \n\t");
    Reject(() => EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "empty"), "empty", "empty document rejected");
    Fixture("version.1.s7dcl", "DATA_BLOCK Test\nEND_DATA_BLOCK");
    Check(EngineeringDocumentDiagnostics.ValidateDocumentImport(root, "version.1").Exists, "dots in legitimate base name remain valid");
    var inner = new InvalidOperationException("line 8 column 19: unexpected token");
    var outer = new Exception("Siemens rejected import", inner);
    var failure = EngineeringDocumentDiagnostics.ImportFailure("ImportFromDocuments", sd, outer);
    Check(ReferenceEquals(failure.InnerException, outer) && failure.Message.Contains(inner.Message) && failure.Message.Contains(sd), "inner exception and file survive outer message serialization");
    var aggregate = new AggregateException(new Exception("first issue"), new Exception("second issue"));
    var aggregateText = EngineeringDocumentDiagnostics.DescribeException(aggregate);
    Check(aggregateText.Contains("first issue") && aggregateText.Contains("second issue"), "all aggregate branches preserved");
    var diagnostic = EngineeringDocumentDiagnostics.DescribeResult(sd, "Failure", new[] { "line 8: invalid declaration", "line 9: unresolved symbol" });
    Check(diagnostic.Contains(sd) && diagnostic.Contains("state=Failure") && diagnostic.Contains("line 8") && diagnostic.Contains("line 9"), "all SDK diagnostics and state retained verbatim");
    Check(EngineeringDocumentDiagnostics.DescribeResult(sd, "PartialSuccess", Array.Empty<string>()).Contains("may already have been imported"), "partial success warns about existing side effects");
    Check(EngineeringDocumentDiagnostics.DescribeResult(sd, "NoResult", Array.Empty<string>()).Contains("no diagnostic messages"), "no diagnostic result stated explicitly");
    // Optional read-only integration with artifacts; does not attach to or invoke TIA.
    foreach (var actual in args)
    {
        var file = new FileInfo(actual);
        Check(EngineeringDocumentDiagnostics.ValidateDocumentImport(file.DirectoryName!, Path.GetFileNameWithoutExtension(file.Name)).Exists,
            "actual artifact preflight: " + file.Name);
    }
    Console.WriteLine($"{passed} offline document diagnostic checks passed.");
}
finally
{
    // Only the unique test-owned directory is removed.
    Directory.Delete(root, true);
}
