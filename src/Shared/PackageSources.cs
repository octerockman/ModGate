using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal sealed class PackageNotFoundException : IOException { public PackageNotFoundException(string id) : base("Package not found: " + id) { } }
    internal interface IUpdateSource : IPackageSource
    {
        Task<PackageId> Latest(PackageId id, CancellationToken token);
    }
    internal interface IPackageLookup : IPackageSource
    {
        Task<List<CatalogEntry>> Candidates(string repository, IReadOnlyList<ModEntry> mods, CancellationToken token);
    }
}
