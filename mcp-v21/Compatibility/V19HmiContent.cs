using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml;

namespace TiaMcpServer.Compatibility
{
    // Pure resolver shared by the reader and offline regression tests. Only the first
    // category segment may be omitted; case, whitespace and punctuation stay literal.
    internal static class V19HmiContentPathResolution
    {
        internal static string Resolve(IEnumerable<string> canonicalPaths, string requested)
        {
            if (string.IsNullOrEmpty(requested) || requested.Any(char.IsControl) ||
                requested.Split('/').Any(p => p.Length == 0 || p == "." || p == ".."))
                throw new ArgumentException("Use an exact slash-separated HMI path without empty/traversal segments or control characters.");
            var paths = canonicalPaths.ToList();
            if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
                throw new InvalidOperationException("Ambiguous canonical HMI record paths; refresh GetHmiScreens before reading content.");
            if (paths.Contains(requested, StringComparer.Ordinal)) return requested;
            var aliases = paths.Where(p => p.IndexOf('/') >= 0 &&
                string.Equals(p.Substring(p.IndexOf('/') + 1), requested, StringComparison.Ordinal)).ToList();
            if (aliases.Count == 1) return aliases[0];
            var candidates = aliases.Count > 1 ? aliases : paths;
            string listed = string.Join(", ", candidates.Take(20).Select(p => "'" + p + "'"));
            if (candidates.Count > 20) listed += ", ... (use GetHmiScreens pagination for all paths)";
            throw new InvalidOperationException((aliases.Count > 1 ? "Ambiguous category-omitted screenPath: " : "HMI path was not found: ") +
                "'" + requested + "'. Canonical candidates: " + (listed.Length == 0 ? "(none)" : listed) +
                ". Use an exact case-sensitive path from GetHmiScreens; no wildcard or regex matching is performed.");
        }
    }

    /// <summary>Reads the official Classic screen Simatic ML export; never edits the project.</summary>
    public static class V19HmiScreenContent
    {
        public const int MaximumExportBytes = 64 * 1024 * 1024;
        public const int MaximumXmlChars = 64 * 1024 * 1024;
        public const int MaximumPageChars = 20000;

        public sealed class ContentPage
        {
            public bool success { get; set; }
            public string source { get; set; } = "Classic V19 screen-object Export / Simatic ML";
            public string representation { get; set; } = "exact XML character slice, not a rendered preview";
            public string scope { get; set; } = "Single screen export only. Layout, objects and bindings are available as serialized by Siemens; referenced tag/connection definitions and external resources are not resolved. A page can start/end inside an XML token and is not necessarily a standalone XML document.";
            public string screenName { get; set; } = "";
            public string sha256 { get; set; } = "";
            public string hashBasis { get; set; } = "SHA256 of original exported bytes including BOM; continuation reads the same in-memory snapshot, valid for 10 minutes or until another screen export replaces it. Snapshot is not live project state.";
            public long totalBytes { get; set; }
            public int totalChars { get; set; }
            public string offsetUnit { get; set; } = "UTF-16 code units in decoded XML, excluding encoding BOM";
            public int offset { get; set; }
            public int returnedChars { get; set; }
            public int? nextOffset { get; set; }
            public bool complete { get; set; }
            public bool endOfDocument { get; set; }
            public string completenessMeaning { get; set; } = "complete means this response contains the entire exported XML; endOfDocument means this page reaches its end. Export completeness is not proof of complete runtime screen semantics.";
            public string xml { get; set; } = "";
            public string error { get; set; } = "";
            public string cleanupWarning { get; set; } = "";
        }

        private static readonly object SnapshotLock = new object();
        private static byte[]? SnapshotBytes;
        private static string SnapshotKey = "";
        private static DateTime SnapshotTime;

        public static bool Supports(object screen) => V19Tools.HmiContentSupported(screen);
        private static void Export(object screen, FileInfo file) => V19Tools.HmiExport(screen, file, "WithReadOnly");

        public static object Read(object screen, string screenName, int offset = 0, int maxChars = 20000, string? expectedSha256 = null, string snapshotKey = "")
        {
            if (screen == null) throw new ArgumentNullException(nameof(screen));
            if (!Supports(screen)) throw new NotSupportedException("This record has no supported Classic V19 XML export.");
            ValidatePage(offset, maxChars, expectedSha256);
            var result = new ContentPage { offset = offset };
            string? ownedDirectory = null;
            try
            {
                result.screenName = screenName.Length <= 1024 ? screenName : screenName.Substring(0, 1024) + " [truncated]";
                snapshotKey += "\n" + screenName;
                if (offset > 0)
                {
                    lock (SnapshotLock)
                    {
                        if (SnapshotBytes == null || SnapshotKey != snapshotKey || DateTime.UtcNow - SnapshotTime > TimeSpan.FromMinutes(10))
                            throw new InvalidOperationException("Screen snapshot expired or was replaced; restart at offset 0.");
                        result = ReadBytes(SnapshotBytes, offset, maxChars, expectedSha256);
                        result.screenName = screenName;
                        return result;
                    }
                }
                // A fresh, user-only directory; neither names nor paths come from project data.
                string path = Path.Combine(Path.GetTempPath(), "tai-classic-screen-" + Guid.NewGuid().ToString("N"));
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var user = identity.User ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
                    security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                }
                if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Temporary export directory already exists.");
                #if NETFRAMEWORK
                Directory.CreateDirectory(path, security);
#else
                new DirectoryInfo(path).Create(security);
#endif
                ownedDirectory = path;
                var file = new FileInfo(Path.Combine(path, "screen.xml"));
                // WithReadOnly includes read-only attributes; Export itself does not modify the project.
                Export(screen, file);
                string displayName = result.screenName;
                result = ReadExport(file, offset, maxChars, expectedSha256);
                result.screenName = displayName;
                lock (SnapshotLock)
                {
                    SnapshotBytes = File.ReadAllBytes(file.FullName);
                    SnapshotKey = snapshotKey;
                    SnapshotTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                result.success = false;
                result.complete = false;
                result.error = DescribeError(ex);
            }
            finally
            {
                if (ownedDirectory != null)
                {
                    try { DeleteOwnedDirectory(ownedDirectory); }
                    catch (Exception ex) { result.cleanupWarning = "Temporary export cleanup failed: " + DescribeError(ex); }
                }
            }
            return result;
        }

        private static string DescribeError(Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            return message.Length <= 2048 ? message : message.Substring(0, 2048) + " [truncated]";
        }

        private static void ValidatePage(int offset, int maxChars, string? expectedSha256)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (maxChars < 1 || maxChars > MaximumPageChars)
                throw new ArgumentOutOfRangeException(nameof(maxChars), "Use 1..20000 UTF-16 code units per page.");
            if (offset > 0 && expectedSha256 == null)
                throw new ArgumentException("Continuation requires expectedSha256 from the first page.", nameof(expectedSha256));
            if (expectedSha256 != null)
            {
                if (expectedSha256.Length != 64) throw new ArgumentException("Expected SHA256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
                foreach (char c in expectedSha256)
                    if (!Uri.IsHexDigit(c)) throw new ArgumentException("Expected SHA256 must contain 64 hexadecimal characters.", nameof(expectedSha256));
            }
        }

        // File-only seam for offline tests. No TIA instance or project is needed.
        internal static ContentPage ReadExport(FileInfo file, int offset, int maxChars, string? expectedSha256)
        {
            ValidatePage(offset, maxChars, expectedSha256);
            byte[] bytes;
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length == 0 || stream.Length > MaximumExportBytes)
                    throw new InvalidDataException("Screen export must be nonempty and at most 64 MiB.");
                bytes = new byte[(int)stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new EndOfStreamException("Screen export changed while reading.");
                    read += count;
                }
                if (stream.ReadByte() != -1) throw new InvalidDataException("Screen export changed while reading.");
            }
            return ReadBytes(bytes, offset, maxChars, expectedSha256);
        }

        internal static ContentPage ReadBytes(byte[] bytes, int offset, int maxChars, string? expectedSha256)
        {
            ValidatePage(offset, maxChars, expectedSha256);
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (expectedSha256 != null && !string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Screen export SHA256 changed. Restart at offset 0; pages cannot be combined. Export metadata can also change between calls.");

            // Validate the whole document, not only the returned page. No DTD or resolver is permitted.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlChars,
                MaxCharactersFromEntities = 1024
            };
            using (var input = new MemoryStream(bytes, false))
            using (var reader = XmlReader.Create(input, settings))
                while (reader.Read()) { }

            // XmlTextReader detects XML declaration/BOM encoding without reserializing whitespace/entities.
            Encoding encoding;
            using (var input = new MemoryStream(bytes, false))
            using (var reader = new XmlTextReader(input) { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })
            {
                reader.MoveToContent();
                encoding = (Encoding)(reader.Encoding ?? throw new InvalidDataException("XML encoding could not be determined.")).Clone();
            }
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            byte[] preamble = encoding.GetPreamble();
            int start = 0;
            if (preamble.Length > 0 && bytes.Length >= preamble.Length)
            {
                bool matches = true;
                for (int i = 0; i < preamble.Length; i++) matches &= bytes[i] == preamble[i];
                if (matches) start = preamble.Length;
            }
            int chars = encoding.GetCharCount(bytes, start, bytes.Length - start);
            if (chars > MaximumXmlChars) throw new InvalidDataException("Decoded screen XML exceeds 64 Mi UTF-16 code units.");
            string xml = encoding.GetString(bytes, start, bytes.Length - start);
            if (offset > xml.Length) throw new ArgumentOutOfRangeException(nameof(offset), "Offset exceeds exported XML length.");
            if (offset > 0 && offset < xml.Length && char.IsLowSurrogate(xml[offset]) && char.IsHighSurrogate(xml[offset - 1]))
                throw new ArgumentException("Offset splits a UTF-16 surrogate pair; use the returned nextOffset.", nameof(offset));
            int length = Math.Min(maxChars, xml.Length - offset);
            if (length > 0 && offset + length < xml.Length && char.IsHighSurrogate(xml[offset + length - 1]) && char.IsLowSurrogate(xml[offset + length]))
                length--;
            if (length == 0 && offset < xml.Length)
                throw new ArgumentException("Page size is too small for the next Unicode character; use maxChars >= 2.", nameof(maxChars));
            int end = offset + length;
            return new ContentPage
            {
                success = true, sha256 = hash, totalBytes = bytes.Length, totalChars = xml.Length,
                offset = offset, returnedChars = length, nextOffset = end < xml.Length ? (int?)end : null,
                complete = offset == 0 && end == xml.Length, endOfDocument = end == xml.Length,
                xml = xml.Substring(offset, length)
            };
        }

        private static void DeleteOwnedDirectory(string path)
        {
            // Never follow a junction or symlink during cleanup, even inside our private directory.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing cleanup of a reparse point.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Refusing cleanup through a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0) DeleteOwnedDirectory(entry);
                else File.Delete(entry);
            }
            Directory.Delete(path, false);
        }
    }
}