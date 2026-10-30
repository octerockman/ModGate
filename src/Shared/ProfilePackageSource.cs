using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModGate
{
    internal sealed class PackageResolutionChangedException : IOException
    {
        public PackageResolutionChangedException(string message) : base(message) { }
    }
    internal sealed class ProfilePackageSource : IPackageSource
    {
        private readonly IPackageSource source;
        private readonly Action<string> log;
        private readonly List<string> issues;
        private readonly Dictionary<string, PackageId> substitutions = new Dictionary<string, PackageId>(StringComparer.OrdinalIgnoreCase);
        public void Trace(string message) => log(message);
        public bool HasSubstitutions => substitutions.Count > 0;
        public ProfilePackageSource(IPackageSource source, Action<string> log, List<string> issues) { this.source = source; this.log = log; this.issues = issues; }
        public PackageId Actual(PackageId id)
        {
            for (var n = 0; n < 128 && substitutions.TryGetValue(id.FullName, out var actual); n++) id = actual;
            return id;
        }
        public bool Substituted(string package) => !string.IsNullOrEmpty(package) && Actual(PackageId.Parse(package)).FullName != PackageId.Parse(package).FullName;
        public Task<PackageMetadata> Metadata(PackageId id, CancellationToken token) => source.Metadata(Actual(id), token);
        public async Task<PackageId> RepairAutomaticDependencyPin(PackageId pin, string minimum, CancellationToken token)
        {
            if (pin.Repository == "github") throw new InvalidDataException("An automatic dependency pin cannot change a manifest asset release.");
            var requested = PackageId.Parse((pin.Repository == "hexium" ? "hexium:" : "") + pin.Key + "-" + minimum);
            var available = await Resolve(requested, false, token).ConfigureAwait(false);
            if (new System.Version(available.Item1.Version) < new System.Version(minimum)) throw new InvalidDataException("No available package meets dependency minimum " + requested.FullName);
            substitutions[pin.FullName] = available.Item1;
            var message = "Automatic dependency mapping " + pin.FullName + " is below required package " + minimum + "; using " + available.Item1.FullName + ". Server DLL requirements are unchanged.";
            issues.Add(message); log(message); return available.Item1;
        }
        private async Task<Tuple<PackageId, PackageMetadata>> Latest(PackageId requested, CancellationToken token, string reason = "Exact release unavailable")
        {
            if (requested.Repository == "github" || !(source is IUpdateSource latest)) throw new PackageNotFoundException(requested.FullName);
            log(reason + ": " + requested.FullName + ". Looking up the latest release on " + requested.Repository + ".");
            var actual = await latest.Latest(requested, token).ConfigureAwait(false);
            if (actual.Repository != requested.Repository || !actual.Key.Equals(requested.Key, StringComparison.OrdinalIgnoreCase) || actual.FullName == requested.FullName) throw new InvalidDataException("No different available release was found for " + requested.FullName + ".");
            var metadata = await source.Metadata(actual, token).ConfigureAwait(false);
            if (metadata.IsActive == false) throw new PackageNotFoundException(actual.FullName);
            substitutions[requested.FullName] = actual;
            var message = "Version substitution: " + reason + " for " + requested.FullName + "; using " + actual.FullName + ". The server will still check the installed plugin versions.";
            issues.Add(message); log(message);
            return Tuple.Create(actual, metadata);
        }
        public async Task<Tuple<PackageId, PackageMetadata>> Resolve(PackageId requested, bool alternate, CancellationToken token)
        {
            var id = Actual(requested);
            log("Resolving " + id.FullName + " from " + id.Repository + ".");
            try { var metadata = await source.Metadata(id, token).ConfigureAwait(false); if (metadata.IsActive == false) throw new PackageNotFoundException(id.FullName); return Tuple.Create(id, metadata); }
            catch (PackageNotFoundException)
            {
                if (alternate && id.Repository != "github")
                {
                    var other = id.FromRepository(id.Repository == "hexium" ? "thunderstore" : "hexium");
                    log("Checking the same dependency version on " + other.Repository + ": " + other.FullName);
                    try { var metadata = await source.Metadata(other, token).ConfigureAwait(false); if (metadata.IsActive == false) throw new PackageNotFoundException(other.FullName); return Tuple.Create(other, metadata); }
                    catch (PackageNotFoundException) { }
                }
                return await Latest(id, token).ConfigureAwait(false);
            }
        }
        public async Task Download(PackageId id, string destination, CancellationToken token)
        {
            id = Actual(id); log("Downloading " + id.FullName + ".");
            try { await source.Download(id, destination, token).ConfigureAwait(false); }
            catch (PackageNotFoundException)
            {
                var replacement = await Latest(id, token).ConfigureAwait(false);
                throw new PackageResolutionChangedException("Download unavailable for " + id.FullName + "; resolving dependencies again for " + replacement.Item1.FullName + ".");
            }
            log("Downloaded " + id.FullName + " (" + new FileInfo(destination).Length + " bytes).");
        }
        public async Task<bool> RepairPluginVersions(Policy policy, IReadOnlyList<ModEntry> actual, CancellationToken token)
        {
            var changed = false;
            foreach (var mod in policy.Mods.Where(m => m.Guid != Constants.Guid && !string.IsNullOrWhiteSpace(m.Package) && !Substituted(m.Package)))
            {
                var found = actual.SingleOrDefault(m => m.Guid == mod.Guid);
                if (found == null || PolicyEngine.AcceptsVersion(policy, mod, found.Version)) continue;
                var id = PackageId.Parse(mod.Package);
                if (id.Repository == "github") continue;
                var reason = "Pinned package contains " + mod.Name + " plugin " + found.Version + ", but the server requires " + mod.Version;
                try { await Latest(id, token, reason).ConfigureAwait(false); changed = true; }
                catch (Exception failure) when (!(failure is OperationCanceledException)) { log(reason + ". Could not select a replacement: " + failure.Message); }
            }
            return changed;
        }
        public Policy ReviewPolicy(Policy original, IReadOnlyList<ModEntry> actual)
        {
            var review = Json.CloneLocal(original);
            foreach (var mod in review.Mods.Where(m => Substituted(m.Package)))
            {
                var found = actual.SingleOrDefault(m => m.Guid == mod.Guid);
                if (found != null) mod.Version = found.Version;
            }
            return review;
        }
    }
}
