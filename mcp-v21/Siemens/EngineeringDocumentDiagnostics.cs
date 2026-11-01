using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace TiaMcpServer.Siemens
{
    // Only format routing is checked here. Siemens owns the SIMATIC SD grammar.
    internal static class EngineeringDocumentDiagnostics
    {
        internal static FileInfo ValidateXmlImport(string path)
        {
            var file = RequireFile(path);
            if (file.Extension.Equals(".s7dcl", StringComparison.OrdinalIgnoreCase) ||
                file.Extension.Equals(".s7res", StringComparison.OrdinalIgnoreCase))
                throw Invalid(file.FullName, "ImportBlock expects SIMATIC ML XML, not SIMATIC SD documents. Use ImportFromDocuments with importPath set to the containing directory and fileNameWithoutExtension set to '" + Path.GetFileNameWithoutExtension(file.Name) + "'. Renaming the file does not convert its format.");
            if (file.Extension.Equals(".scl", StringComparison.OrdinalIgnoreCase))
                throw Invalid(file.FullName, "ImportBlock expects SIMATIC ML XML. SCL external source requires the external-source generation workflow; renaming it does not convert its format.");
            try
            {
                using (var reader = XmlReader.Create(file.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                {
                    reader.MoveToContent();
                    if (reader.LocalName != "Document" || reader.NamespaceURI.Length != 0)
                        throw Invalid(file.FullName, "ImportBlock expects a SIMATIC ML XML Document root. Export a PLC block as XML to obtain the correct format.");
                    while (reader.Read()) { }
                }
            }
            catch (XmlException ex)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "ImportBlock file '" + file.FullName + "' is not well-formed SIMATIC ML XML: " + ex.Message +
                    " Use ImportFromDocuments for .s7dcl SIMATIC SD documents.", null, ex);
            }
            return file;
        }

        internal static FileInfo ValidateDocumentImport(string directory, string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 ||
                new[] { ".s7dcl", ".s7res", ".xml", ".scl" }.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "ImportFromDocuments requires fileNameWithoutExtension to be a document base name, without a directory or .s7dcl/.s7res/.xml/.scl suffix. Received '" + name + "' in '" + directory + "'.");
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new PortalException(PortalErrorCode.InvalidParams,
                    "ImportFromDocuments importPath must be an existing directory, not a file: '" + directory + "'.");
            var file = RequireFile(Path.Combine(directory, name + ".s7dcl"));
            using (var reader = new StreamReader(file.FullName, true))
            {
                int first;
                do { first = reader.Read(); } while (first >= 0 && char.IsWhiteSpace((char)first));
                if (first < 0) throw Invalid(file.FullName, "SIMATIC SD document is empty.");
                if (first == '<') throw Invalid(file.FullName,
                    "ImportFromDocuments expects SIMATIC SD .s7dcl text, but this file starts with XML markup. Use ImportBlock for a SIMATIC ML XML export. Renaming XML to .s7dcl does not convert it.");
            }
            // Do not demand an .s7res companion: exports without resources are valid.
            // Do not parse SCL-looking statements: SIMATIC SD also represents LAD/FBD.
            return file;
        }

        private static FileInfo RequireFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new PortalException(PortalErrorCode.InvalidParams, "Import file path is required.");
            var file = new FileInfo(path);
            if (!file.Exists) throw new PortalException(PortalErrorCode.NotFound, "Import file does not exist: '" + file.FullName + "'.");
            return file;
        }

        private static PortalException Invalid(string path, string message) =>
            new PortalException(PortalErrorCode.InvalidParams, "Import file '" + path + "': " + message);

        internal static string DescribeException(Exception exception)
        {
            var messages = new List<string>();
            AppendException(exception, messages);
            return string.Join(" --> ", messages);
        }

        private static void AppendException(Exception exception, List<string> messages)
        {
            messages.Add(exception.GetType().FullName + ": " + exception.Message);
            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) AppendException(inner, messages);
            }
            else if (exception.InnerException != null) AppendException(exception.InnerException, messages);
        }

        internal static PortalException ImportFailure(string operation, string path, Exception exception) =>
            new PortalException(PortalErrorCode.InvalidState,
                operation + " failed for file '" + path + "'. " + DescribeException(exception), null, exception);

        internal static string DescribeResult(string path, string state, IEnumerable<string> messages)
        {
            var details = messages.Where(message => !string.IsNullOrWhiteSpace(message)).ToArray();
            return "ImportFromDocuments file '" + path + "' (SIMATIC SD .s7dcl), Siemens state=" + state + ". " +
                (details.Length == 0 ? "Siemens returned no diagnostic messages." : "Siemens diagnostics: " + string.Join(Environment.NewLine, details)) +
                (state == "PartialSuccess" ? " Some blocks may already have been imported; inspect the project before retrying." : "");
        }
    }
}
