using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ModGate;

internal static partial class Program
{
    private static byte[] UpdateAssembly(string name, string version)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = new Version(version + ".0") }, typeof(object).Assembly);
        assembly.DefineDynamicModule("Fixture").DefineType("MetadataOnly", TypeAttributes.Public).CreateType();
        using var bytes = new MemoryStream(); assembly.Save(bytes); return bytes.ToArray();
    }
    private static string UpdatePut(string root, string relative, byte[] bytes)
    { var path = SafePath.Under(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path; }
    private static byte[] UpdateZip(string version = "0.4.8", bool patcher = true, string? extra = null, string identity = "ValheimModGate")
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            using (var file = zip.CreateEntry("BepInEx/plugins/ValheimModGate/ValheimModGate.dll").Open()) file.Write(UpdateAssembly(identity, version));
            if (patcher) using (var file = zip.CreateEntry("BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll").Open()) file.Write(UpdateAssembly("ValheimModGate.Preloader", version));
            if (extra != null) using (var file = new StreamWriter(zip.CreateEntry(extra).Open())) file.Write("untrusted");
        }
        return bytes.ToArray();
    }
    private static (string root, string plugin, string patcher) UpdateRoot()
    {
        var root = Root();
        var plugin = UpdatePut(root, "plugins/Manager-ModGate/renamed-plugin.dll", UpdateAssembly("ValheimModGate", Constants.Version));
        var patcher = UpdatePut(root, "patchers/Manager-ModGate/renamed-preloader.dll", UpdateAssembly("ValheimModGate.Preloader", Constants.Version));
        Put(root, "config/user.cfg", "keep-config"); Put(root, "ModGate/profiles/offline/keep.txt", "keep-profile");
        return (root, plugin, patcher);
    }
    private static SelfUpdateApiRelease ApiUpdate(byte[] bytes, string version = "0.4.8", bool prerelease = false)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        return new SelfUpdateApiRelease { Tag = "v" + version, Prerelease = prerelease, Assets = new List<SelfUpdateApiAsset> { new SelfUpdateApiAsset { Name = "ValheimModGate-" + version + "-publish.zip", Url = SelfUpdateSource.Repository + "/releases/download/v" + version + "/ValheimModGate-" + version + "-publish.zip", Digest = "sha256:" + digest, Size = bytes.Length, State = "uploaded" } } };
    }
    private static SelfUpdateSource UpdateSource(byte[] payload, List<Uri>? requests = null, string? metadata = null)
        => new SelfUpdateSource((request, token) =>
        {
            requests?.Add(request.Url);
            var bytes = request.Url.Host == "api.github.com" && metadata != null ? Encoding.UTF8.GetBytes(metadata) : payload;
            return Task.FromResult(new SystemResponse { StatusCode = 200, Body = new MemoryStream(bytes), Headers = new Dictionary<string, string> { ["Content-Length"] = bytes.Length.ToString() } });
        });
    private static SelfUpdatePlan UpdatePlan(string version = "0.4.8")
    {
        var (root, plugin, patcher) = UpdateRoot(); var bytes = UpdateZip(version);
        return UpdateSource(bytes).Prepare(root, SelfUpdateSource.Select(ApiUpdate(bytes, version), false), plugin, patcher, CancellationToken.None).GetAwaiter().GetResult();
    }
    private static async Task SelfUpdateChecks()
    {
        Test("self-update chooses only the official runtime ZIP and requires GitHub SHA-256", () =>
        {
            var listing = ApiUpdate(UpdateZip()); listing.Assets.Insert(0, new SelfUpdateApiAsset { Name = "ValheimModGate-0.4.8-source.zip" });
            Check(SelfUpdateSource.Select(listing, false).Asset == "ValheimModGate-0.4.8-publish.zip");
            listing.Assets[1].Digest = ""; Throws<InvalidDataException>(() => SelfUpdateSource.Select(listing, false));
        });
        Test("self-update refuses drafts, unselected early releases, foreign assets and duplicate runtime assets", () =>
        {
            var listing = ApiUpdate(UpdateZip(), prerelease: true);
            Throws<InvalidDataException>(() => SelfUpdateSource.Select(listing, false)); Check(SelfUpdateSource.Select(listing, true).Prerelease);
            listing.Draft = true; Throws<InvalidDataException>(() => SelfUpdateSource.Select(listing, true)); listing.Draft = false;
            listing.Assets[0].Url = "https://github.com/other/ModGate/releases/download/v0.4.8/runtime.zip"; Throws<InvalidDataException>(() => SelfUpdateSource.Select(listing, true));
            listing = ApiUpdate(UpdateZip()); listing.Assets.Add(listing.Assets[0]); Throws<InvalidDataException>(() => SelfUpdateSource.Select(listing, false));
        });
        Test("server connector version selects exact official release and rejects injected version text", () =>
        {
            var policy = Policy(Mod(Constants.Guid, version: "0.4.8.0"));
            Check(SelfUpdateSource.RequiredByServer(policy) == "0.4.8"); policy.Mods[0].Version = Constants.Version; Check(SelfUpdateSource.RequiredByServer(policy) == null);
            policy.Mods[0].Version = "../../other"; Throws<InvalidDataException>(() => SelfUpdateSource.RequiredByServer(policy));
            Check(!SelfUpdateSource.Trusted(new Uri("https://github.com.evil.test/file")) && !SelfUpdateSource.Trusted(new Uri("http://github.com/file")));
        });
        var queries = new List<Uri>(); var api = ApiUpdate(UpdateZip(), prerelease: true);
        var found = await UpdateSource(Array.Empty<byte>(), queries, Json.Write(api)).Find("0.4.8", true, CancellationToken.None);
        Test("server version lookup uses the exact tag endpoint rather than latest", () => Check(found.Version == "0.4.8" && queries.Single().AbsolutePath.EndsWith("/releases/tags/v0.4.8")));
        var listingJson = Json.Write(new List<SelfUpdateApiRelease> { ApiUpdate(UpdateZip("0.4.9"), "0.4.9", true), ApiUpdate(UpdateZip(), "0.4.8") });
        var stable = await UpdateSource(Array.Empty<byte>(), metadata: listingJson).Find(null, false, CancellationToken.None);
        Test("stable channel does not silently install a newer early release", () => Check(stable.Version == "0.4.8"));
        var plan = UpdatePlan();
        Test("preparation verifies both DLLs and keeps installed files, configs and profiles unchanged", () =>
        {
            Check(plan.Files.Count == 2 && plan.Files[0].Target == "plugins/Manager-ModGate/renamed-plugin.dll" && plan.Files[1].Target == "patchers/Manager-ModGate/renamed-preloader.dll");
            foreach (var file in plan.Files) Check(SafePath.Hash(SafePath.Under(plan.Root, file.Target)) == file.PreviousSha256 && SafePath.Hash(SafePath.Under(plan.Root, file.Staged)) == file.Sha256);
            Check(File.ReadAllText(SafePath.Under(plan.Root, "config/user.cfg")) == "keep-config" && File.ReadAllText(SafePath.Under(plan.Root, "ModGate/profiles/offline/keep.txt")) == "keep-profile");
        });
        foreach (var issue in new[] { "hash", "one-dll", "wrong-identity", "extra-code", "traversal" })
        {
            var (root, plugin, patcher) = UpdateRoot();
            var bytes = issue == "one-dll" ? UpdateZip(patcher: false) : issue == "wrong-identity" ? UpdateZip(identity: "OtherMod") : issue == "extra-code" ? UpdateZip(extra: "extra.dll") : issue == "traversal" ? UpdateZip(extra: "../escape.txt") : UpdateZip();
            var release = SelfUpdateSource.Select(ApiUpdate(bytes), false);
            if (issue == "hash") release.Sha256 = new string('0', 64);
            Test("self-update rejects " + issue + " without changing installed components", () =>
            {
                var before = SafePath.Hash(plugin); var previous = SafePath.Hash(patcher);
                Throws<Exception>(() => UpdateSource(bytes).Prepare(root, release, plugin, patcher, CancellationToken.None).GetAwaiter().GetResult());
                Check(SafePath.Hash(plugin) == before && SafePath.Hash(patcher) == previous && !File.Exists(Path.Combine(root, "escape.txt")) && !File.Exists(SafePath.Under(root, SelfUpdateInstaller.Active)));
            });
        }
        Test("staged DLL modification is rejected before queuing a self-update", () =>
        {
            var changed = UpdatePlan(); File.AppendAllText(SafePath.Under(changed.Root, changed.Files[0].Staged), "tamper");
            Throws<InvalidDataException>(() => SelfUpdateInstaller.Validate(changed.Root, changed, true));
            Check(!File.Exists(SafePath.Under(changed.Root, SelfUpdateInstaller.Active)));
        });
        Test("duplicate connector copies are reported without automatic duplicate cleanup", () =>
        {
            var (root, _, _) = UpdateRoot(); UpdatePut(root, "plugins/OldManager/old.dll", UpdateAssembly("ValheimModGate", Constants.Version));
            Throws<InvalidDataException>(() => SelfUpdateInstaller.RequireSinglePair(root)); Check(File.Exists(SafePath.Under(root, "plugins/OldManager/old.dll")));
        });
        Test("server self-update stays off by default and reconciliation preserves explicit choices", () =>
        {
            var settings = new ServerManifest(); Check(!settings.AutoUpdateModGate && !settings.AutoUpdateOnRestart);
            settings.AutoUpdateModGate = true; settings.ModGateIncludePrereleases = false;
            var reconciled = AutoSetup.Reconcile(settings, Array.Empty<ModEntry>(), Array.Empty<string>());
            Check(reconciled.AutoUpdateModGate && !reconciled.ModGateIncludePrereleases && !reconciled.AutoUpdateOnRestart);
        });
        Test("startup guard refuses an interrupted pair and an old preloader already loaded during replacement", () =>
        {
            var queued = UpdatePlan(); var root = queued.Root; Json.Save(SafePath.Under(root, SelfUpdateInstaller.Active), queued);
            var first = queued.Files[0]; File.Copy(SafePath.Under(root, first.Staged), SafePath.Under(root, first.Target), true);
            Check(SelfUpdateInstaller.GuardStartup(root, _ => { }));
            var second = queued.Files[1]; File.Copy(SafePath.Under(root, second.Staged), SafePath.Under(root, second.Target), true);
            Check(SelfUpdateInstaller.GuardStartup(root, _ => { }));
            var matching = UpdatePlan(Constants.Version); Json.Save(SafePath.Under(matching.Root, SelfUpdateInstaller.Active), matching);
            foreach (var f in matching.Files) File.Copy(SafePath.Under(matching.Root, f.Staged), SafePath.Under(matching.Root, f.Target), true);
            Check(!SelfUpdateInstaller.GuardStartup(matching.Root, _ => { }));
        });
        Test("Linux helper uses a process identity, file lock, verified atomic copies and no process killing", () =>
        {
            var queued = UpdatePlan(); queued.ProcessId = 123; queued.ProcessStart = "456";
            var text = SelfUpdateInstaller.LinuxHelper(queued);
            Check(text.Contains("flock") && text.Contains("sha256sum") && text.Contains("process_identity") && text.Contains("copy_verified") && !text.Contains("kill "));
            var bash = @"C:\Program Files\Git\bin\bash.exe";
            if (File.Exists(bash))
            {
                var path = Path.Combine(queued.Root, "helper-syntax.sh"); File.WriteAllText(path, text, new UTF8Encoding(false));
                using var syntax = Process.Start(new ProcessStartInfo(bash, "-n " + RestartLauncher.QuoteArgument(path.Replace('\\', '/'))) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
                Check(syntax != null && syntax.WaitForExit(10000) && syntax.ExitCode == 0);
            }
        });
    }
    private static void SelfUpdateHelperChecks()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) { Console.WriteLine("Windows helper integration requires Windows."); return; }
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        foreach (var scenario in new[] { "normal", "corrupted-stage", "interrupted-pair", "normal-restart", "locked-second-target" })
        {
            var plan = UpdatePlan(); var root = plan.Root;
            using var owner = Process.Start(new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 3\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
            plan.ProcessId = owner.Id; plan.ProcessStart = SelfUpdateInstaller.ProcessIdentity(owner.Id);
            if (scenario == "corrupted-stage") File.AppendAllText(SafePath.Under(root, plan.Files[0].Staged), "modified");
            if (scenario == "interrupted-pair")
            {
                foreach (var f in plan.Files) File.Copy(SafePath.Under(root, f.Target), SafePath.Under(root, f.Backup));
                File.Copy(SafePath.Under(root, plan.Files[0].Staged), SafePath.Under(root, plan.Files[0].Target), true);
            }
            File.WriteAllText(SelfUpdateInstaller.ScriptPath(root, plan), SelfUpdateInstaller.WindowsHelper);
            Json.Save(SafePath.Under(root, SelfUpdateInstaller.Active), plan); File.WriteAllText(SafePath.Under(root, SelfUpdateInstaller.Marker), plan.Id);
            using var heldTarget = scenario == "locked-second-target" ? new FileStream(SafePath.Under(root, plan.Files[1].Target), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            var restart = new RestartPlan { ProcessId = owner.Id, StartTicks = owner.StartTime.ToUniversalTime().Ticks, Executable = powershell,
                WorkingDirectory = root, ConfirmationId = Guid.NewGuid().ToString("N"), ExpectedVersion = plan.Version,
                SelfUpdatePlan = SafePath.Under(root, SelfUpdateInstaller.Active) };
            restart.Arguments = "-NoProfile -NonInteractive -Command " + RestartLauncher.QuoteArgument("$receipt = @{ Version = '" + plan.Version + "'; Ready = $true; Error = '' } | ConvertTo-Json; [IO.File]::WriteAllText((Join-Path $env:MODGATE_RESTART_ROOT ('ModGate/restart-confirmed-' + $env:MODGATE_RESTART_TOKEN + '.json')), $receipt)");
            using var helper = scenario == "normal-restart" ? RestartLauncher.Start(root, restart) : SelfUpdateInstaller.StartHelper(root, plan);
            if (scenario == "normal") foreach (var f in plan.Files) Check(SafePath.Hash(SafePath.Under(root, f.Target)) == f.PreviousSha256);
            if (!helper.WaitForExit(45000)) throw new Exception("Self-update helper timeout.");
            var success = scenario == "normal" || scenario == "normal-restart";
            var error = SafePath.Under(root, "ModGate/self-update-error.txt");
            Test("actual hidden self-update helper " + scenario, () =>
            {
                if (success && helper.ExitCode != 0) throw new Exception(File.Exists(error) ? File.ReadAllText(error) : File.Exists(Path.Combine(root,"ModGate/restart-error.txt")) ? File.ReadAllText(Path.Combine(root,"ModGate/restart-error.txt")) : "No self-update diagnostic");
                Check(success ? helper.ExitCode == 0 : helper.ExitCode != 0);
                foreach (var f in plan.Files) Check(SafePath.Hash(SafePath.Under(root, f.Target)) == (success ? f.Sha256 : f.PreviousSha256));
                if (success) foreach (var f in plan.Files) Check(SafePath.Hash(SafePath.Under(root, f.Backup)) == f.PreviousSha256);
                Check(!File.Exists(SafePath.Under(root, SelfUpdateInstaller.Active)));
                if (scenario == "normal-restart") Check(File.ReadAllText(Path.Combine(root, "ModGate/restart-status.txt")).Contains("Confirmed:"));
                Check(File.ReadAllText(SafePath.Under(root, "config/user.cfg")) == "keep-config" && File.ReadAllText(SafePath.Under(root, "ModGate/profiles/offline/keep.txt")) == "keep-profile");
            });
        }
    }
    private static async Task SelfUpdateLiveCheck(string version)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var source = new SelfUpdateSource(); var release = await source.Find(version, true, timeout.Token);
        var (root, plugin, patcher) = UpdateRoot();
        var plan = await source.Prepare(root, release, plugin, patcher, timeout.Token);
        Check(plan.Files.Count == 2 && plan.Files.All(f => SafePath.Hash(SafePath.Under(root, f.Target)) == f.PreviousSha256));
        Console.WriteLine("PASS official GitHub metadata, SHA-256 download and both runtime DLL identities: " + release.Version + " / " + release.Asset + " / " + release.Sha256 + ". Installed files were not changed.");
    }
}
