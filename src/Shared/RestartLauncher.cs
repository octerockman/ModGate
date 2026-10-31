using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace ModGate
{
    [DataContract] internal sealed class RestartPlan
    {
        [DataMember] public int ProcessId;
        [DataMember] public long StartTicks;
        [DataMember] public string Executable = "";
        [DataMember] public string Arguments = "";
        [DataMember] public string WorkingDirectory = "";
        [DataMember] public string BootstrapAssembly = "";
        [DataMember] public string ConfirmationId = "";
        [DataMember] public string ExpectedVersion = "";
        [DataMember] public string SelfUpdatePlan = "";
    }
    [DataContract] internal sealed class RestartConfirmation
    {
        [DataMember] public string Version = "";
        [DataMember] public bool Ready;
        [DataMember] public string Error = "";
    }
    internal static class RestartLauncher
    {
        // The helper waits for normal shutdown. It never kills the game or edits profiles.
        internal const string Helper = @"param([Parameter(Mandatory=$true)][string]$Plan)
$ErrorActionPreference = 'Stop'
$folder = [System.IO.Path]::GetDirectoryName($Plan)
try {
    $restart = Get-Content -LiteralPath $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
    $old = Get-Process -Id $restart.ProcessId -ErrorAction SilentlyContinue
    if ($old) {
        if ($old.StartTime.ToUniversalTime().Ticks -ne [long]$restart.StartTicks) { throw 'The original process identity changed; restart cancelled.' }
        if (-not $old.WaitForExit(120000)) { throw 'Valheim did not close within two minutes. Close and relaunch it manually.' }
    }
    if ($restart.SelfUpdatePlan) {
        $expected = Join-Path $folder 'self-update-active.json'
        if ([IO.Path]::GetFullPath($restart.SelfUpdatePlan) -cne [IO.Path]::GetFullPath($expected)) { throw 'Invalid self-update restart plan.' }
        $update = Get-Content -LiteralPath $expected -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($update.Id -notmatch '^[a-fA-F0-9]{32}$' -or $update.Version -cne $restart.ExpectedVersion) { throw 'Self-update version or identity changed.' }
        $updateHelper = Join-Path $folder ('self-updates/' + $update.Id + '/apply-update.ps1')
        $apply = New-Object System.Diagnostics.ProcessStartInfo
        $apply.FileName = Join-Path $PSHOME 'powershell.exe'
        $apply.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""' + $updateHelper + '"" -Plan ""' + $expected + '""'
        $apply.UseShellExecute = $false
        $apply.CreateNoWindow = $true
        $worker = [Diagnostics.Process]::Start($apply)
        $worker.WaitForExit()
        if ($worker.ExitCode -ne 0) { throw 'ModGate self-update did not finish. Check self-update-error.txt before relaunching.' }
    }
    $launch = New-Object System.Diagnostics.ProcessStartInfo
    $launch.FileName = $restart.Executable
    $launch.Arguments = $restart.Arguments
    $launch.WorkingDirectory = $restart.WorkingDirectory
    $launch.UseShellExecute = $false
    $launch.CreateNoWindow = $true
    # Runtime guard flags belong to the old process. Keep configuration/manager variables.
    $launch.EnvironmentVariables.Remove('DOORSTOP_INITIALIZED')
    $launch.EnvironmentVariables.Remove('DOORSTOP_DISABLE')
    if ($restart.BootstrapAssembly -and -not [System.IO.File]::Exists($restart.BootstrapAssembly)) { throw 'The active BepInEx preloader is missing. Relaunch from your mod manager.' }
    if ($restart.ConfirmationId) {
        $launch.EnvironmentVariables['MODGATE_RESTART_TOKEN'] = $restart.ConfirmationId
        $launch.EnvironmentVariables['MODGATE_RESTART_ROOT'] = [System.IO.Path]::GetDirectoryName($folder)
    }
    $next = [System.Diagnostics.Process]::Start($launch)
    if (-not $next) { throw 'Valheim could not be relaunched.' }
    if ($restart.ConfirmationId) {
        Set-Content -LiteralPath (Join-Path $folder 'restart-status.txt') -Value 'Valheim started; waiting for BepInEx and ModGate.'
        $confirmation = Join-Path $folder ('restart-confirmed-' + $restart.ConfirmationId + '.json')
        $deadline = [DateTime]::UtcNow.AddMinutes(3)
        while (-not [System.IO.File]::Exists($confirmation)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'Valheim started but BepInEx/ModGate did not confirm loading within three minutes. Relaunch from your mod manager; the scheduled profile change is kept.' }
            Start-Sleep -Milliseconds 250
        }
        $result = Get-Content -LiteralPath $confirmation -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($result.Version -ne $restart.ExpectedVersion) { throw 'A different ModGate version loaded after restart. Update both ModGate DLLs in the active mod manager profile.' }
        if (-not $result.Ready) { throw $result.Error }
        Remove-Item -LiteralPath $confirmation -Force
        Set-Content -LiteralPath (Join-Path $folder 'restart-status.txt') -Value 'Confirmed: BepInEx and ModGate loaded; profile setup is ready.'
    } else {
        Set-Content -LiteralPath (Join-Path $folder 'restart-status.txt') -Value 'Relaunch requested successfully.'
    }
    $previousError = Join-Path $folder 'restart-error.txt'
    if (Test-Path -LiteralPath $previousError) { Remove-Item -LiteralPath $previousError -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $Plan -Force -ErrorAction SilentlyContinue
} catch {
    Set-Content -LiteralPath (Join-Path $folder 'restart-error.txt') -Value $_.Exception.Message
    exit 1
}
";

        public static string QuoteArgument(string value)
        {
            // Windows CommandLineToArgvW quoting, including trailing slashes and embedded quotes.
            var result = new System.Text.StringBuilder("\""); int slashes = 0;
            foreach (var ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { result.Append('\\', slashes * 2 + 1); result.Append(ch); slashes = 0; continue; }
                result.Append('\\', slashes); slashes = 0; result.Append(ch);
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }

        public static void BeginCurrent(string root)
        {
            using (var current = Process.GetCurrentProcess())
            {
                var executable = current.MainModule?.FileName ?? throw new IOException("Cannot identify this Valheim installation. Restart it manually.");
                if (!Path.GetFileName(executable).Equals("valheim.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Automatic restart currently supports the Windows Valheim client. Restart this installation manually.");
                using (var helper = Start(root, CurrentPlan(root, executable, current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.GetCommandLineArgs().Skip(1).ToArray(), Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH")))) { }
            }
        }

        internal static string ModdedArguments(string[] arguments, string bootstrap, bool modern)
        {
            var settings = new[] { "--doorstop-enable", "--doorstop-enabled", "--doorstop_enabled", "--doorstop-target", "--doorstop-target-assembly", "--doorstop_target_assembly" };
            var kept = new System.Collections.Generic.List<string>();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (settings.Contains(arguments[i], StringComparer.OrdinalIgnoreCase)) { if (i + 1 < arguments.Length) i++; continue; }
                kept.Add(arguments[i]);
            }
            kept.Add(modern ? "--doorstop-enabled" : "--doorstop-enable"); kept.Add("true");
            kept.Add(modern ? "--doorstop-target-assembly" : "--doorstop-target"); kept.Add(bootstrap);
            return string.Join(" ", kept.Select(QuoteArgument));
        }
        internal static RestartPlan CurrentPlan(string root, string executable, int processId, long startTicks, string[] arguments, string? invokedAssembly)
        {
            var working = Path.GetDirectoryName(Path.GetFullPath(executable))!;
            var bootstrap = string.IsNullOrWhiteSpace(invokedAssembly) ? Path.Combine(root, "core", "BepInEx.Preloader.dll") : Path.IsPathRooted(invokedAssembly) ? invokedAssembly! : Path.Combine(working, invokedAssembly!);
            bootstrap = Path.GetFullPath(bootstrap);
            if (!File.Exists(bootstrap) || new FileInfo(bootstrap).Length == 0) throw new IOException("Cannot locate the active BepInEx preloader. Relaunch from your mod manager.");
            var version = Path.Combine(working, ".doorstop_version");
            var modern = File.Exists(version) ? System.Version.TryParse(File.ReadAllText(version).Trim(), out var nativeVersion) && nativeVersion.Major >= 4 : !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOORSTOP_INITIALIZED"));
            return new RestartPlan { ProcessId = processId, StartTicks = startTicks, Executable = executable, WorkingDirectory = working,
                BootstrapAssembly = bootstrap, ConfirmationId = Guid.NewGuid().ToString("N"), ExpectedVersion = Constants.Version,
                Arguments = ModdedArguments(arguments, bootstrap, modern) };
        }
        internal static void CleanRuntimeFlags(ProcessStartInfo start)
        {
            foreach (var key in start.EnvironmentVariables.Keys.Cast<string>().Where(k => k.Equals("DOORSTOP_INITIALIZED", StringComparison.OrdinalIgnoreCase) || k.Equals("DOORSTOP_DISABLE", StringComparison.OrdinalIgnoreCase)).ToArray()) start.EnvironmentVariables.Remove(key);
        }
        public static void ConfirmCurrent(string root)
        {
            var token = Environment.GetEnvironmentVariable("MODGATE_RESTART_TOKEN");
            var expected = Environment.GetEnvironmentVariable("MODGATE_RESTART_ROOT");
            if (!Guid.TryParseExact(token, "N", out _) || string.IsNullOrEmpty(expected) || !Path.GetFullPath(root).TrimEnd('\\', '/').Equals(Path.GetFullPath(expected).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return;
            var ready = !Profiles.Pending(root) && !File.Exists(SafePath.Under(root, "ModGate/install-error.txt"));
            Json.Save(SafePath.Under(root, "ModGate/restart-confirmed-" + token + ".json"), new RestartConfirmation { Version = Constants.Version, Ready = ready, Error = ready ? "" : "BepInEx and ModGate loaded, but the profile change did not finish. Check BepInEx/ModGate/install-error.txt and the recovery journal." });
            Environment.SetEnvironmentVariable("MODGATE_RESTART_TOKEN", null);
            Environment.SetEnvironmentVariable("MODGATE_RESTART_ROOT", null);
        }

        internal static Process Start(string root, RestartPlan plan)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new IOException("Automatic restart currently supports Windows. Quit and relaunch Valheim manually.");
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell) || !File.Exists(plan.Executable) || !Directory.Exists(plan.WorkingDirectory)) throw new IOException("The restart launcher is unavailable. Quit and relaunch Valheim manually.");
            if (plan.ConfirmationId.Length > 0 && !Guid.TryParseExact(plan.ConfirmationId, "N", out _)) throw new IOException("Invalid restart confirmation identity.");
            var script = SafePath.Under(root, "ModGate/restart-helper.ps1");
            var file = SafePath.Under(root, "ModGate/restart-" + Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, Helper);
            Json.Save(file, plan);
            var start = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + QuoteArgument(script) + " -Plan " + QuoteArgument(file))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(script)! };
            CleanRuntimeFlags(start);
            return Process.Start(start) ?? throw new IOException("Could not start the restart helper. Quit and relaunch Valheim manually.");
        }
    }
}
