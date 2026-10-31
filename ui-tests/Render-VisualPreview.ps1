[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '../../work/ui043-preview'),
    [string]$BrowserPath = '',
    [string[]]$Surfaces = @('mods', 'dependencies', 'profiles', 'mod-details', 'mod-detail', 'configs', 'server-configs', 'server-health', 'server-health-narrow', 'server-health-history', 'download', 'creators', 'creators-narrow', 'creator-popup', 'creator-popup-narrow')
)
$ErrorActionPreference = 'Stop'
$previewDirectory = (Resolve-Path -LiteralPath $OutputPath).Path
if (!$BrowserPath) {
    $BrowserPath = @(
        "$env:ProgramFiles/Google/Chrome/Application/chrome.exe",
        "${env:ProgramFiles(x86)}/Microsoft/Edge/Application/msedge.exe",
        "$env:LOCALAPPDATA/Microsoft/Edge/Application/msedge.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$BrowserPath) { throw 'Pass -BrowserPath pointing to Chrome or Edge to rasterize the local draw replay.' }
$browserExecutable = (Resolve-Path -LiteralPath $BrowserPath).Path
$temporaryDirectory = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$browserProfile = Join-Path $temporaryDirectory ('ModGate-ui-replay-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($browserProfile)
try {
    foreach ($surface in $Surfaces) {
        $document = (Resolve-Path -LiteralPath (Join-Path $previewDirectory ($surface + '.html'))).Path
        $metadata = Get-Content -LiteralPath (Join-Path $previewDirectory ($surface + '.json')) -Raw | ConvertFrom-Json
        $viewportWidth = [int]$metadata.viewport.width
        $viewportHeight = [int]$metadata.viewport.height
        if ($viewportWidth -lt 1 -or $viewportWidth -gt 8192 -or $viewportHeight -lt 1 -or $viewportHeight -gt 8192) { throw 'Unsupported preview viewport dimensions.' }
        $screenshot = Join-Path $previewDirectory ($surface + '.png')
        $start = [Diagnostics.ProcessStartInfo]::new($browserExecutable)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        # Only generated local replay HTML is opened; DNS/background networking are disabled.
        foreach ($argument in @('--headless', '--disable-gpu', '--in-process-gpu', '--disable-gpu-sandbox', '--no-sandbox', '--disable-background-networking', '--host-resolver-rules=MAP * 0.0.0.0', '--no-first-run', '--no-default-browser-check', '--hide-scrollbars', "--window-size=$viewportWidth,$viewportHeight", "--user-data-dir=$browserProfile", "--screenshot=$screenshot", ([Uri]$document).AbsoluteUri)) {
            $start.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            [void]$process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            [void]$stdout.GetAwaiter().GetResult()
            $diagnostics = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $screenshot)) { throw "Local replay rasterization failed: $diagnostics" }
            Write-Output $screenshot
        } finally { $process.Dispose() }
    }
} finally {
    $resolvedProfile = [IO.Path]::GetFullPath($browserProfile)
    if (!$resolvedProfile.StartsWith($temporaryDirectory, [StringComparison]::OrdinalIgnoreCase) -or !([IO.Path]::GetFileName($resolvedProfile)).StartsWith('ModGate-ui-replay-', [StringComparison]::Ordinal)) {
        throw 'Temporary browser profile path validation failed.'
    }
    Remove-Item -LiteralPath $resolvedProfile -Recurse -Force -ErrorAction SilentlyContinue
}
