using System;
using System.IO;

namespace TAI.TiaAddIn
{
    /// <summary>
    /// Fixed per-user install location produced by the TAI installer:
    /// %LOCALAPPDATA%\Programs\TAI\TAI.Desktop.exe
    /// </summary>
    internal static class DesktopLocator
    {
        internal const string RelativeInstallDir = @"Programs\TAI";
        internal const string DesktopExeName = "TAI.Desktop.exe";

        internal static string GetDesktopExecutablePath()
        {
            string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(localAppData))
            {
                return null;
            }

            return Path.Combine(localAppData, RelativeInstallDir, DesktopExeName);
        }
    }
}
