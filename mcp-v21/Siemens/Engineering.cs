using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TiaMcpServer.Siemens
{
    // Resolve only the selected SDK layout. Never scan other installed versions.
    public class Engineering
    {
#if TIA_V19
        public const int BuildMajorVersion = 19;
#else
        public const int BuildMajorVersion = 21;
#endif
        public static int TiaMajorVersion { get; set; } = BuildMajorVersion;

        public static IEnumerable<string> AssemblyCandidates(string root, int version, string name)
        {
            yield return Path.Combine(root, "PublicAPI", "V" + version, "net48", name + ".dll");
            yield return Path.Combine(root, "PublicAPI", "V" + version, name + ".dll");
            // Never serve Bin\PublicAPI here: those are the portal's internal runtime assemblies
            // (Contract/ClientAdapter). Loading them makes TiaPortal.Attach() take the portal-side
            // transport and fail with "Connection to TiaPortal failed."
        }

        public static Assembly? Resolver(object sender, ResolveEventArgs args)
        {
            var requested = new AssemblyName(args.Name);
            if (requested.Name == null || !(requested.Name == "Siemens.Engineering" || requested.Name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))) return null;
            if (requested.Version != null && requested.Version.Major != TiaMajorVersion)
                throw new FileLoadException($"Requested SDK {requested.FullName} does not match TIA V{TiaMajorVersion}.");
            foreach (var root in InstallationRoots())
                foreach (var candidate in AssemblyCandidates(root, TiaMajorVersion, requested.Name))
                    if (File.Exists(candidate))
                    {
                        var actual = AssemblyName.GetAssemblyName(candidate);
                        if (actual.Version.Major != TiaMajorVersion || actual.Name != requested.Name)
                            throw new FileLoadException($"SDK version mismatch at {candidate}: {actual.FullName}");
                        return Assembly.LoadFrom(candidate);
                    }
            // Optional dependencies (for example Siemens.Engineering.Contract) are probed by the SDK
            // itself; returning null lets it fall back to the supported client transport.
            return null;
        }

        public static IEnumerable<string> InstallationRoots()
        {
            var configured = Environment.GetEnvironmentVariable("TiaPortalLocation");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                yield return Path.GetFullPath(configured);
                yield break; // Explicit configuration must not silently select another installation.
            }
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                {
                    using (var installed = key.OpenSubKey($@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{TiaMajorVersion}\TIA_Opns"))
                    {
                        var root = installed?.GetValue("Path") as string;
                        if (!string.IsNullOrWhiteSpace(root)) yield return root!;
                    }
                    var suffix = TiaMajorVersion >= 20 ? @"\net48" : "";
                    using (var api = key.OpenSubKey($@"SOFTWARE\Siemens\Automation\Openness\{TiaMajorVersion}.0\PublicAPI\{TiaMajorVersion}.0.0.0{suffix}"))
                    {
                        var assembly = api?.GetValue(TiaMajorVersion >= 20 ? "Siemens.Engineering.Base" : "Siemens.Engineering") as string;
                        if (!string.IsNullOrWhiteSpace(assembly))
                        {
                            var directory = new DirectoryInfo(Path.GetDirectoryName(assembly));
                            if (TiaMajorVersion >= 20) directory = directory.Parent;
                            if (directory?.Parent?.Parent != null) yield return directory.Parent.Parent.FullName;
                        }
                    }
                }
            }
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Siemens", "Automation", "Portal V" + TiaMajorVersion);
        }
    }
}
