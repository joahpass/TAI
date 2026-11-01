using System.Security.Principal;
using System.Threading.Tasks;

namespace TiaMcpServer.Siemens
{
    public static class Openness
    {
        // Assembly resolution is owned by Engineering.Resolver for both SDK layouts.
        public const string GroupName = "Siemens TIA Openness";

        // Check the Windows token without initializing a Siemens API session during startup.
        public static Task<bool> IsUserInGroup()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                foreach (IdentityReference reference in identity.Groups)
                {
                    try
                    {
                        var account = reference.Translate(typeof(NTAccount)).Value;
                        if (account.EndsWith("\\" + GroupName, System.StringComparison.OrdinalIgnoreCase))
                            return Task.FromResult(true);
                    }
                    catch (IdentityNotMappedException) { }
                }
            }
            return Task.FromResult(false);
        }

        public static string GroupHelp()
            => "Windows user is not in the '" + GroupName + "' group. Add it in lusrmgr.msc (local users and groups), then sign out and in again.";
    }
}
