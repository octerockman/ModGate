using System.Collections.Generic;
using System.Linq;

namespace ModGate
{
    internal enum ModStatus { Installed, MissingRequired, Attention }
    internal static class ModStatuses
    {
        public static ModStatus For(ModEntry wanted, IReadOnlyList<ModEntry> installed, Policy? policy = null)
        {
            var found = installed.FirstOrDefault(m => m.Guid == wanted.Guid);
            if (found != null && PolicyEngine.AcceptsVersion(policy ?? new Policy(), wanted, found.Version)) return ModStatus.Installed;
            return found == null && PolicyEngine.Required(wanted) ? ModStatus.MissingRequired : ModStatus.Attention;
        }
    }
}
