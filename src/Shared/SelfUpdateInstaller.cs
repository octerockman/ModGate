using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ModGate
{
    internal static class SelfUpdateInstaller
    {
        internal const string Active = "ModGate/self-update-active.json", Marker = "ModGate/self-update-active.id";
        internal static bool Supported => Environment.OSVersion.Platform == PlatformID.Win32NT ||
            File.Exists("/proc/self/stat") && File.Exists("/bin/sh") && File.Exists("/usr/bin/flock") && File.Exists("/usr/bin/sha256sum");
        internal static string Target(string root, string absolute, bool preloader)
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            absolute = Path.GetFullPath(absolute);
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ModGate is outside the active BepInEx installation. Update it through your mod manager.");
            var relative = absolute.Substring(prefix.Length).Replace('\\', '/');
            if (!relative.StartsWith(preloader ? "patchers/" : "plugins/", StringComparison.OrdinalIgnoreCase) || !relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Place the ModGate preloader under patchers and the plugin under plugins before self-updating.");
            SafePath.Under(root, relative);
            return relative;
        }
        internal static void RequireSinglePair(string root)
        {
            var counts = new Dictionary<string, int> { ["ValheimModGate"] = 0, ["ValheimModGate.Preloader"] = 0 };
            foreach (var branch in new[] { "plugins", "patchers" })
            {
                var start = SafePath.Under(root, branch);
                if (!Directory.Exists(start)) continue;
                var pending = new Stack<string>(); pending.Push(start);
                while (pending.Count > 0)
                {
                    var directory = pending.Pop();
                    foreach (var child in Directory.GetDirectories(directory)) if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    foreach (var file in Directory.GetFiles(directory).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        try { var name = System.Reflection.AssemblyName.GetAssemblyName(file).Name; if (name != null && counts.ContainsKey(name)) counts[name]++; }
                        catch (Exception error) when (error is IOException || error is BadImageFormatException || error is UnauthorizedAccessException) { }
                    }
                }
            }
            if (counts.Any(p => p.Value != 1)) throw new InvalidDataException("Self-update needs exactly one plugin and one preloader in this BepInEx installation. Check duplicate or misplaced ModGate copies in your mod manager; no copies were removed.");
        }
        internal static void Validate(string root, SelfUpdatePlan plan, bool current)
        {
            if (plan == null || !Guid.TryParseExact(plan.Id, "N", out _) || Path.GetFullPath(plan.Root) != Path.GetFullPath(root) || plan.Files == null || plan.Files.Count != 2 ||
                SelfUpdateSource.NormalizeVersion(plan.Version) != plan.Version || SelfUpdateSource.NormalizeVersion(plan.PreviousVersion) != plan.PreviousVersion) throw new InvalidDataException("Invalid ModGate self-update plan.");
            for (var i = 0; i < 2; i++)
            {
                var f = plan.Files[i]; var assembly = i == 0 ? "ValheimModGate.dll" : "ValheimModGate.Preloader.dll";
                if (f == null || f.Target != Target(root, SafePath.Under(root, f.Target), i == 1) ||
                    f.Staged != "ModGate/self-updates/" + plan.Id + "/" + assembly || f.Backup != "ModGate/self-updates/" + plan.Id + "/backup-" + assembly ||
                    !Regex.IsMatch(f.Sha256 ?? "", @"^[a-f0-9]{64}$") || !Regex.IsMatch(f.PreviousSha256 ?? "", @"^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid ModGate update file receipt.");
                SafePath.Under(root, f.Backup);
                var staged = SafePath.Under(root, f.Staged);
                if (!File.Exists(staged) || SafePath.Hash(staged) != f.Sha256) throw new InvalidDataException("The staged ModGate update changed. Download it again.");
                SelfUpdateSource.VerifyAssembly(staged, i == 1, plan.Version);
                if (current)
                {
                    var target = SafePath.Under(root, f.Target);
                    if (!File.Exists(target) || SafePath.Hash(target) != f.PreviousSha256) throw new InvalidDataException("An installed ModGate DLL changed after staging. Check your mod manager and download again.");
                    SelfUpdateSource.VerifyAssembly(target, i == 1, plan.PreviousVersion);
                }
            }
        }
        internal static string ProcessIdentity(int id)
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                using (var process = Process.GetProcessById(id)) return process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var line = File.ReadAllText("/proc/" + id + "/stat");
            var parts = line.Substring(line.LastIndexOf(')') + 2).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts[19];
        }
        internal static string ScriptPath(string root, SelfUpdatePlan plan) => SafePath.Under(root, "ModGate/self-updates/" + plan.Id + "/apply-update." + (Environment.OSVersion.Platform == PlatformID.Win32NT ? "ps1" : "sh"));
        internal static void Arm(string root, SelfUpdatePlan plan, bool restart)
        {
            if (!Supported) throw new IOException("Self-updating supports Windows PowerShell or Linux with /proc, sh, flock and sha256sum. Install this release manually on this system.");
            if (Profiles.Pending(root)) throw new IOException("Finish the pending profile change before updating ModGate.");
            Validate(root, plan, true);
            if (File.Exists(SafePath.Under(root, Active))) throw new IOException("A ModGate update is already queued. Close the game/server or cancel that update first.");
            using (var current = Process.GetCurrentProcess())
            {
                plan.ProcessId = current.Id; plan.ProcessStart = ProcessIdentity(current.Id);
                RestartPlan? launch = null;
                if (restart)
                {
                    if (Environment.OSVersion.Platform != PlatformID.Win32NT || !string.Equals(Path.GetFileName(current.MainModule?.FileName), "valheim.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Automatic relaunch supports the Windows Valheim client. Choose installation after closing instead.");
                    launch = RestartLauncher.CurrentPlan(root, current.MainModule!.FileName!, current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.GetCommandLineArgs().Skip(1).ToArray(), Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH"));
                    launch.ExpectedVersion = plan.Version;
                    launch.SelfUpdatePlan = SafePath.Under(root, Active);
                }
                var script = ScriptPath(root, plan);
                File.WriteAllText(script, Environment.OSVersion.Platform == PlatformID.Win32NT ? WindowsHelper : LinuxHelper(plan));
                Json.Save(SafePath.Under(root, Active), plan);
                File.WriteAllText(SafePath.Under(root, Marker), plan.Id);
                try
                {
                    using (var helper = launch == null ? StartHelper(root, plan) : RestartLauncher.Start(root, launch)) { }
                }
                catch { Cancel(root, plan.Id); throw; }
            }
        }
        internal static void Cancel(string root, string id)
        {
            var marker = SafePath.Under(root, Marker);
            if (File.Exists(marker) && File.ReadAllText(marker) == id)
            {
                File.Delete(marker);
                var active = SafePath.Under(root, Active);
                if (File.Exists(active)) File.Delete(active);
            }
        }
        internal static Process StartHelper(string root, SelfUpdatePlan plan)
        {
            var script = ScriptPath(root, plan);
            if (!File.Exists(script)) throw new IOException("The self-update helper is missing. Restore both ModGate DLLs from the backup before restarting.");
            var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            var executable = windows ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh";
            var args = windows ? "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + RestartLauncher.QuoteArgument(script) + " -Plan " + RestartLauncher.QuoteArgument(SafePath.Under(root, Active)) : RestartLauncher.QuoteArgument(script);
            var start = new ProcessStartInfo(executable, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(script)! };
            RestartLauncher.CleanRuntimeFlags(start);
            return Process.Start(start) ?? throw new IOException("Could not start the self-update helper.");
        }
        // A hosting panel can relaunch immediately after shutdown. Never load a mixed DLL pair.
        // The external helper can resume an interrupted transaction using the retained backups.
        internal static bool GuardStartup(string root, Action<string> log)
        {
            var active = SafePath.Under(root, Active);
            if (!File.Exists(active))
            {
                // The helper can finish and remove its receipt while BepInEx is loading us.
                // Compare metadata without loading the replacement into this process.
                var loaded = typeof(SelfUpdateInstaller).Assembly;
                if (loaded.GetName().Name != "ValheimModGate.Preloader") return false;
                try
                {
                    if (System.Reflection.AssemblyName.GetAssemblyName(loaded.Location).Version == loaded.GetName().Version) return false;
                    log("ModGate was updated while its previous preloader was loading. Restart once more before loading the world.");
                }
                catch (Exception failure) { log("Cannot verify the loaded ModGate preloader: " + failure.Message + ". Restart after checking both ModGate DLLs."); }
                return true;
            }
            try
            {
                var plan = Json.Read<SelfUpdatePlan>(File.ReadAllText(active)); Validate(root, plan, false);
                if (plan.Files.All(f => File.Exists(SafePath.Under(root, f.Target)) && SafePath.Hash(SafePath.Under(root, f.Target)) == f.Sha256))
                {
                    if (Constants.Version == plan.Version) return false;
                    log("The ModGate DLL pair was updated after this process loaded its preloader. Restart once more so both loaded components use " + plan.Version + ".");
                    return true;
                }
                using (var helper = StartHelper(root, plan)) { }
                log("A ModGate self-update is applying or recovering. This early launch will stop before loading the world. Wait for BepInEx/ModGate/self-update-status.txt, then restart.");
            }
            catch (Exception error) { log("Cannot finish the queued ModGate self-update: " + error.Message + ". Preserve BepInEx/ModGate/self-updates and restore its two backup DLLs together before retrying."); }
            return true;
        }

        internal const string WindowsHelper = @"param([Parameter(Mandatory=$true)][string]$Plan)
$ErrorActionPreference = 'Stop'
$update = Get-Content -LiteralPath $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
$selfRoot = [IO.Path]::GetFullPath($update.Root).TrimEnd('\','/')
$selfFolder = Join-Path $selfRoot 'ModGate'
$marker = Join-Path $selfFolder 'self-update-active.id'
$lock = $null
$changed = $false
$committed = $false
function Checked([string]$relative) {
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')) { throw 'Invalid update path.' }
    $parts = $relative.Replace('\','/').Split('/')
    $path = $selfRoot
    foreach ($part in @('') + $parts) {
        if ($part -eq '' -and $path -eq $selfRoot) { }
        elseif ($part -eq '' -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ')) { throw 'Invalid update path component.' }
        else { $path = Join-Path $path $part }
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Self-update cannot use symbolic links.' }
    }
    return $path
}
function Hash([string]$path) {
    if (-not [IO.File]::Exists($path)) { return '' }
    $stream = [IO.File]::OpenRead($path); $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function CurrentMarker { return [IO.File]::Exists($marker) -and [IO.File]::ReadAllText($marker) -ceq $update.Id }
function PairIs([string]$field) { foreach ($item in $update.Files) { if ((Hash (Checked $item.Target)) -cne $item.$field) { return $false } }; return $true }
function AtomicCopy([string]$source, [string]$target, [string]$expected) {
    $temp = $target + '.modgate-' + $update.Id + '.tmp'
    for ($try = 0; $try -lt 120; $try++) {
        try {
            if ((Hash $source) -cne $expected) { throw 'Update source checksum changed.' }
            $null = Checked ($temp.Substring($selfRoot.Length + 1))
            [IO.File]::Copy($source, $temp, $true)
            if ((Hash $temp) -cne $expected) { throw 'Update copy checksum failed.' }
            if ([IO.File]::Exists($target)) { [IO.File]::Replace($temp, $target, [System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($temp, $target) }
            return
        } catch { if ($try -eq 119) { throw }; Start-Sleep -Milliseconds 100 }
    }
}
try {
    if ($update.Id -notmatch '^[a-fA-F0-9]{32}$' -or $update.Version -notmatch '^[0-9]+[.][0-9]+[.][0-9]+$' -or $update.Files.Count -ne 2 -or [IO.Path]::GetFullPath($Plan) -cne (Join-Path $selfFolder 'self-update-active.json')) { throw 'Invalid self-update plan.' }
    for ($try = 0; $try -lt 120; $try++) {
        try { $lock = [IO.File]::Open((Checked 'ModGate/self-update.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); break }
        catch { if ($try -eq 119) { exit 2 }; Start-Sleep -Milliseconds 100 }
    }
    $null = Checked 'ModGate/self-update-active.json'
    $null = Checked 'ModGate/self-update-active.id'
    if (-not (CurrentMarker)) { exit 0 }
    $old = Get-Process -Id $update.ProcessId -ErrorAction SilentlyContinue
    if ($old -and $old.StartTime.ToUniversalTime().Ticks.ToString() -eq $update.ProcessStart) {
        while (-not $old.WaitForExit(1000)) { if (-not (CurrentMarker)) { exit 0 } }
    }
    if (-not (CurrentMarker)) { exit 0 }
    for ($i = 0; $i -lt 2; $i++) {
        $item = $update.Files[$i]
        $base = if ($i -eq 0) { 'plugins/' } else { 'patchers/' }
        $assembly = if ($i -eq 0) { 'ValheimModGate' } else { 'ValheimModGate.Preloader' }
        if (-not $item.Target.StartsWith($base, [StringComparison]::OrdinalIgnoreCase) -or -not $item.Target.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) -or $item.Staged -cne ('ModGate/self-updates/' + $update.Id + '/' + $assembly + '.dll') -or $item.Backup -cne ('ModGate/self-updates/' + $update.Id + '/backup-' + $assembly + '.dll') -or $item.Sha256 -notmatch '^[a-f0-9]{64}$' -or $item.PreviousSha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid self-update file receipt.' }
        $stage = Checked $item.Staged
        if ((Hash $stage) -cne $item.Sha256) { throw 'Staged ModGate checksum changed.' }
        $identity = [Reflection.AssemblyName]::GetAssemblyName($stage)
        if ($identity.Name -cne $assembly -or $identity.Version.ToString() -ne ($update.Version + '.0')) { throw 'Wrong staged ModGate assembly.' }
        $null = Checked $item.Target
        $null = Checked $item.Backup
    }
    if (PairIs 'Sha256') { $committed = $true }
    elseif (-not (PairIs 'PreviousSha256')) {
        foreach ($item in $update.Files) { if ((Hash (Checked $item.Backup)) -cne $item.PreviousSha256) { throw 'Interrupted update has no verified backup pair. Restore both DLLs manually.' } }
        $changed = $true
        throw 'Interrupted update detected; restoring the previous DLL pair.'
    } else {
        foreach ($item in $update.Files) { [IO.File]::Copy((Checked $item.Target), (Checked $item.Backup), $true); if ((Hash (Checked $item.Backup)) -cne $item.PreviousSha256) { throw 'ModGate backup checksum failed.' } }
        $changed = $true
        foreach ($item in $update.Files) { AtomicCopy (Checked $item.Staged) (Checked $item.Target) $item.Sha256 }
        $committed = $true
    }
    if (CurrentMarker) { [IO.File]::Delete($marker); [IO.File]::Delete($Plan) }
    [IO.File]::WriteAllText((Checked 'ModGate/self-update-status.txt'), ('Installed ModGate ' + $update.Version + '. Both DLLs verified. Configs and profiles were kept. Backups: ModGate/self-updates/' + $update.Id))
    $previousError = Checked 'ModGate/self-update-error.txt'
    if ([IO.File]::Exists($previousError)) { [IO.File]::Delete($previousError) }
} catch {
    $failure = $_.Exception.Message
    if ($changed -and -not $committed) {
        try { foreach ($item in $update.Files) { AtomicCopy (Checked $item.Backup) (Checked $item.Target) $item.PreviousSha256 } }
        catch { $failure += ' Rollback requires attention: ' + $_.Exception.Message }
    }
    try { if (($committed -or (PairIs 'PreviousSha256')) -and (CurrentMarker)) { [IO.File]::Delete($marker); [IO.File]::Delete($Plan) } } catch { }
    [IO.File]::WriteAllText((Checked 'ModGate/self-update-error.txt'), $failure)
    exit 1
} finally { if ($lock) { $lock.Dispose() } }
";
        private static string Shell(string value)
        {
            if (value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new InvalidDataException("Unsupported self-update path.");
            return "'" + value.Replace("'", "'\"'\"'") + "'";
        }
        internal static string LinuxHelper(SelfUpdatePlan plan)
        {
            var lines = new List<string> { "#!/bin/sh", "set -eu", "PATH=/usr/bin:/bin; export PATH", "umask 077", "root=" + Shell(plan.Root), "identity=" + Shell(plan.Id), "owner_pid=" + plan.ProcessId, "owner_start=" + Shell(plan.ProcessStart),
                "marker=\"$root/ModGate/self-update-active.id\"", "active=\"$root/ModGate/self-update-active.json\"", "changed=0; committed=0" };
            for (var i = 0; i < 2; i++)
            {
                var f = plan.Files[i];
                lines.Add("target" + i + "=" + Shell(SafePath.Under(plan.Root, f.Target))); lines.Add("stage" + i + "=" + Shell(SafePath.Under(plan.Root, f.Staged))); lines.Add("backup" + i + "=" + Shell(SafePath.Under(plan.Root, f.Backup)));
                lines.Add("hash" + i + "=" + Shell(f.Sha256)); lines.Add("oldhash" + i + "=" + Shell(f.PreviousSha256));
            }
            lines.Add("""
current() { [ -f "$marker" ] && [ "$(cat "$marker")" = "$identity" ]; }
checked() { value=$1; while [ "$value" != / ]; do [ ! -L "$value" ] || return 1; value=${value%/*}; [ -n "$value" ] || value=/; done; }
hash() { [ -f "$1" ] || { printf missing; return; }; /usr/bin/sha256sum "$1" | cut -d ' ' -f 1; }
pair_old() { [ "$(hash "$target0")" = "$oldhash0" ] && [ "$(hash "$target1")" = "$oldhash1" ]; }
pair_new() { [ "$(hash "$target0")" = "$hash0" ] && [ "$(hash "$target1")" = "$hash1" ]; }
copy_verified() { checked "$1" && checked "$2" || return 1; [ "$(hash "$1")" = "$3" ] || return 1; temp="$2.modgate-$identity.tmp"; checked "$temp" || return 1; cp -p "$1" "$temp" || return 1; [ "$(hash "$temp")" = "$3" ] || return 1; mv -f "$temp" "$2"; }
finish() {
  result=$?; trap - EXIT
  if [ "$result" -ne 0 ] && [ "$changed" = 1 ] && [ "$committed" = 0 ]; then copy_verified "$backup0" "$target0" "$oldhash0" && copy_verified "$backup1" "$target1" "$oldhash1" || true; fi
  if [ "$committed" = 1 ] || pair_old; then if current; then rm -f "$marker" "$active"; fi; fi
  if [ "$result" -ne 0 ]; then printf '%s\n' 'ModGate self-update failed. Previous DLLs were restored when verified backups were available. See ModGate/self-updates; keep both DLLs together.' > "$root/ModGate/self-update-error.txt"; fi
  exit "$result"
}
process_identity() { [ -r "/proc/$1/stat" ] || return 1; stat_line=$(cat "/proc/$1/stat") || return 1; stat_line=${stat_line##*) }; set -- $stat_line; [ "$#" -ge 20 ] || return 1; shift 19; printf '%s' "$1"; }
checked "$root/ModGate/self-update.lock" || exit 1
exec 9>"$root/ModGate/self-update.lock"
/usr/bin/flock -w 12 9 || exit 2
for file in "$marker" "$active" "$root/ModGate/self-update-status.txt" "$root/ModGate/self-update-error.txt"; do checked "$file" || exit 1; done
trap finish EXIT
current || exit 0
while [ "$(process_identity "$owner_pid" || true)" = "$owner_start" ]; do current || exit 0; sleep 1; done
current || exit 0
for file in "$target0" "$target1" "$stage0" "$stage1" "$backup0" "$backup1"; do checked "$file" || exit 1; done
[ "$(hash "$stage0")" = "$hash0" ] && [ "$(hash "$stage1")" = "$hash1" ] || exit 1
if pair_new; then committed=1
elif pair_old; then
  cp -p "$target0" "$backup0" && cp -p "$target1" "$backup1"
  [ "$(hash "$backup0")" = "$oldhash0" ] && [ "$(hash "$backup1")" = "$oldhash1" ] || exit 1
  changed=1
  copy_verified "$stage0" "$target0" "$hash0"
  copy_verified "$stage1" "$target1" "$hash1"
  committed=1
else
  [ "$(hash "$backup0")" = "$oldhash0" ] && [ "$(hash "$backup1")" = "$oldhash1" ] || exit 1
  changed=1; exit 1
fi
printf '%s\n' 'Installed the verified ModGate DLL pair. Configs and profiles were kept; backups remain in ModGate/self-updates.' > "$root/ModGate/self-update-status.txt"
rm -f "$root/ModGate/self-update-error.txt"
""");
            return string.Join("\n", lines).Replace("\r\n", "\n");
        }
    }
}
