#requires -Version 7.0
# Build a local upload ZIP. This script does not publish or contact a website.
param(
    [string]$GameDirectory,
    [string]$BepInExCoreDirectory,
    [switch]$UseExistingBuild,
    [switch]$NoRestore,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$modGateRoot = $PSScriptRoot
$modGateUtf8 = [Text.UTF8Encoding]::new($false, $true)
$modGateProperties = [xml][IO.File]::ReadAllText((Join-Path $modGateRoot 'Directory.Build.props'), $modGateUtf8)
$modGateVersion = [string]$modGateProperties.Project.PropertyGroup.Version
if ($modGateVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The release version must use Major.Minor.Patch.' }
if (-not $UseExistingBuild) {
    if (-not $GameDirectory) { throw 'Supply GameDirectory to build, or use UseExistingBuild after validating the Release DLLs.' }
    $modGateBuildOptions = @{ GameDirectory = $GameDirectory }
    if ($BepInExCoreDirectory) { $modGateBuildOptions.BepInExCoreDirectory = $BepInExCoreDirectory }
    if ($NoRestore) { $modGateBuildOptions.NoRestore = $true }
    if ($SkipTests) { $modGateBuildOptions.SkipTests = $true }
    & (Join-Path $modGateRoot 'build.ps1') @modGateBuildOptions
}

$modGateManifestPath = Join-Path $modGateRoot 'publishing/manifest.json'
$modGateManifest = [IO.File]::ReadAllText($modGateManifestPath, $modGateUtf8) | ConvertFrom-Json
$modGateManifestFields = @($modGateManifest.PSObject.Properties.Name | Sort-Object)
if (($modGateManifestFields -join ',') -ne 'dependencies,description,name,version_number,website_url') { throw 'The package manifest must contain the five supported fields.' }
if ($modGateManifest.name -isnot [string] -or $modGateManifest.name -cnotmatch '^[A-Za-z0-9_]{1,128}$') { throw 'Invalid package name.' }
if ($modGateManifest.version_number -cne $modGateVersion) { throw 'The package and build versions differ.' }
if ($modGateManifest.description -isnot [string] -or $modGateManifest.description.Length -eq 0 -or $modGateManifest.description.Length -gt 250) { throw 'The package description must be 1–250 characters.' }
$modGateWebsite = $null
if ($modGateManifest.website_url -isnot [string] -or -not [Uri]::TryCreate($modGateManifest.website_url, [UriKind]::Absolute, [ref]$modGateWebsite) -or $modGateWebsite.Scheme -cne 'https') { throw 'Use an absolute HTTPS support/homepage URL.' }
if ($modGateManifest.dependencies -isnot [array]) { throw 'Manifest dependencies must be an array.' }
foreach ($modGateDependency in $modGateManifest.dependencies) {
    if ($modGateDependency -isnot [string] -or $modGateDependency -cnotmatch '^[A-Za-z0-9_]+-[A-Za-z0-9_]+-\d+\.\d+\.\d+$') { throw 'Invalid package dependency.' }
}

$modGateIconPath = Join-Path $modGateRoot 'publishing/icon.png'
$modGateIcon = [IO.File]::ReadAllBytes($modGateIconPath)
if ($modGateIcon.Length -lt 33 -or [Convert]::ToHexString($modGateIcon[0..7]) -cne '89504E470D0A1A0A' -or [Text.Encoding]::ASCII.GetString($modGateIcon, 12, 4) -cne 'IHDR') { throw 'The package icon must be a PNG image.' }
if ([Convert]::ToHexString($modGateIcon[16..23]) -cne '0000010000000100') { throw 'The icon must be exactly 256 by 256 pixels.' }

$modGatePackageFiles = [ordered]@{
    'manifest.json' = 'publishing/manifest.json'
    'icon.png' = 'publishing/icon.png'
    'README.md' = 'README.md'
    'CHANGELOG.md' = 'publishing/CHANGELOG.md'
    'LICENSE' = 'LICENSE'
    'server-mods.example.json' = 'examples/server-mods.example.json'
    'docs/PLAYER-GUIDE.md' = 'docs/PLAYER-GUIDE.md'
    'docs/SERVER-OWNER-GUIDE.md' = 'docs/SERVER-OWNER-GUIDE.md'
    'docs/TECHNICAL-REFERENCE.md' = 'docs/TECHNICAL-REFERENCE.md'
    'BepInEx/plugins/ValheimModGate/ValheimModGate.dll' = 'src/ValheimModGate/bin/Release/netstandard2.1/ValheimModGate.dll'
    'BepInEx/patchers/ValheimModGate/ValheimModGate.Preloader.dll' = 'src/ValheimModGate.Preloader/bin/Release/netstandard2.0/ValheimModGate.Preloader.dll'
}
foreach ($modGateItem in $modGatePackageFiles.GetEnumerator()) {
    $modGateSource = Join-Path $modGateRoot $modGateItem.Value
    if (-not (Test-Path -LiteralPath $modGateSource -PathType Leaf) -or (Get-Item -LiteralPath $modGateSource).Length -eq 0) { throw "Missing or empty release file: $($modGateItem.Value)" }
    if ($modGateItem.Key.EndsWith('.dll')) {
        # Read metadata without loading or executing either mod assembly.
        $modGateAssembly = [Reflection.AssemblyName]::GetAssemblyName($modGateSource)
        $modGateExpectedName = [IO.Path]::GetFileNameWithoutExtension($modGateItem.Key)
        if ($modGateAssembly.Name -cne $modGateExpectedName -or $modGateAssembly.Version.ToString() -cne ($modGateVersion + '.0')) { throw 'A Release DLL has the wrong identity or version.' }
    }
    elseif ($modGateItem.Key.EndsWith('.md')) {
        $modGateDocument = [IO.File]::ReadAllText($modGateSource, $modGateUtf8)
        if ($modGateDocument -match '(?i)<YOUR_|\[TODO\]|\bPLACEHOLDER\b') { throw "Unfinished publication text: $($modGateItem.Key)" }
    }
}
$modGateExample = [IO.File]::ReadAllText((Join-Path $modGateRoot 'examples/server-mods.example.json'), $modGateUtf8) | ConvertFrom-Json
if ($modGateExample.autoUpdateOnRestart -ne $false -or $modGateExample.autoUpdateAllMods -ne $false) { throw 'Server updating must stay off by default.' }

$modGateOutput = Join-Path $modGateRoot "dist/publishing/$modGateVersion"
New-Item -ItemType Directory -Force -Path $modGateOutput | Out-Null
$modGateZipPath = Join-Path $modGateOutput "ValheimModGate-$modGateVersion-publish.zip"
$modGateStream = [IO.File]::Open($modGateZipPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$modGateArchive = [IO.Compression.ZipArchive]::new($modGateStream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($modGateItem in $modGatePackageFiles.GetEnumerator()) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($modGateArchive, (Join-Path $modGateRoot $modGateItem.Value), $modGateItem.Key, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $modGateArchive.Dispose(); $modGateStream.Dispose() }

$modGateCheck = [IO.Compression.ZipFile]::OpenRead($modGateZipPath)
try {
    if ($modGateCheck.Entries.Count -ne $modGatePackageFiles.Count) { throw 'Unexpected files in upload package.' }
    foreach ($modGateEntry in $modGateCheck.Entries) {
        if (-not $modGatePackageFiles.Contains($modGateEntry.FullName)) { throw 'An unapproved file entered the package.' }
        $modGateEntryStream = $modGateEntry.Open()
        try { $modGateArchiveHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($modGateEntryStream)) }
        finally { $modGateEntryStream.Dispose() }
        $modGateExpectedHash = (Get-FileHash -LiteralPath (Join-Path $modGateRoot $modGatePackageFiles[$modGateEntry.FullName]) -Algorithm SHA256).Hash
        if ($modGateArchiveHash -cne $modGateExpectedHash) { throw 'A packaged file does not match its release source.' }
    }
}
finally { $modGateCheck.Dispose() }
$modGateHash = (Get-FileHash -LiteralPath $modGateZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $modGateOutput 'SHA256SUMS.txt'), "$modGateHash  $([IO.Path]::GetFileName($modGateZipPath))`n", $modGateUtf8)
Write-Output "Upload package: $modGateZipPath"
Write-Output "Validated $($modGatePackageFiles.Count) allowlisted files, matching DLL versions, manifest, PNG dimensions and archive hashes."
Write-Output "Description: $($modGateManifest.description.Length)/250 characters. No upload was performed."
