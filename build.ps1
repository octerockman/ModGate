#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$GameDirectory,
    [string]$BepInExCoreDirectory,
    [switch]$SkipTests,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$modGateProjectRoot = $PSScriptRoot
$modGateManaged = Join-Path $GameDirectory 'valheim_Data/Managed'
if (-not $BepInExCoreDirectory) { $BepInExCoreDirectory = Join-Path $GameDirectory 'BepInEx/core' }
foreach ($modGateRequired in @((Join-Path $modGateManaged 'assembly_valheim.dll'), (Join-Path $BepInExCoreDirectory 'BepInEx.dll'), (Join-Path $BepInExCoreDirectory '0Harmony.dll'), (Join-Path $BepInExCoreDirectory 'Mono.Cecil.dll'))) {
    if (-not (Test-Path -LiteralPath $modGateRequired) -or (Get-Item -LiteralPath $modGateRequired).Length -eq 0) { throw "A working game/loader reference is required: $modGateRequired" }
}
$modGateProperties = @("-p:GameManagedPath=$modGateManaged", "-p:BepInExPath=$BepInExCoreDirectory")
$modGateRestoreOption = @()
if ($NoRestore) { $modGateRestoreOption = @('--no-restore') }
foreach ($modGateProject in @('src/ValheimModGate/ValheimModGate.csproj', 'src/ValheimModGate.Preloader/ValheimModGate.Preloader.csproj')) {
    & dotnet build (Join-Path $modGateProjectRoot $modGateProject) -c Release @modGateProperties @modGateRestoreOption
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $modGateProject" }
}
if (-not $SkipTests) {
    & dotnet run --project (Join-Path $modGateProjectRoot 'tests/ModGate.Tests.csproj') -c Release @modGateRestoreOption
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    & dotnet run --project (Join-Path $modGateProjectRoot 'ui-tests/Prompt.Tests.csproj') -c Release @modGateRestoreOption
    if ($LASTEXITCODE -ne 0) { throw 'Prompt verification failed.' }
    & dotnet run --project (Join-Path $modGateProjectRoot 'input-tests/Input.Tests.csproj') -c Release @modGateRestoreOption
    if ($LASTEXITCODE -ne 0) { throw 'Gameplay input and config reload verification failed.' }
    & dotnet run --project (Join-Path $modGateProjectRoot 'server-tests/Server.Tests.csproj') -c Release @modGateProperties @modGateRestoreOption
    if ($LASTEXITCODE -ne 0) { throw 'Server update verification failed.' }
    & dotnet run --project (Join-Path $modGateProjectRoot 'inventory-tests/Inventory.Tests.csproj') -c Release @modGateProperties @modGateRestoreOption
    if ($LASTEXITCODE -ne 0) { throw 'Inventory verification failed.' }
}
$modGateDist = Join-Path $modGateProjectRoot 'dist/0.4.4'
$modGatePluginDestination = Join-Path $modGateDist 'BepInEx/plugins/ValheimModGate'
$modGatePatcherDestination = Join-Path $modGateDist 'BepInEx/patchers/ValheimModGate'
New-Item -ItemType Directory -Force -Path $modGatePluginDestination, $modGatePatcherDestination | Out-Null
Copy-Item -LiteralPath (Join-Path $modGateProjectRoot 'src/ValheimModGate/bin/Release/netstandard2.1/ValheimModGate.dll') -Destination $modGatePluginDestination -Force
Copy-Item -LiteralPath (Join-Path $modGateProjectRoot 'src/ValheimModGate.Preloader/bin/Release/netstandard2.0/ValheimModGate.Preloader.dll') -Destination $modGatePatcherDestination -Force
Copy-Item -LiteralPath (Join-Path $modGateProjectRoot 'README.md') -Destination $modGateDist -Force
Copy-Item -LiteralPath (Join-Path $modGateProjectRoot 'LICENSE') -Destination $modGateDist -Force
Copy-Item -LiteralPath (Join-Path $modGateProjectRoot 'examples/server-mods.example.json') -Destination $modGateDist -Force
$modGateDocsDestination = Join-Path $modGateDist 'docs'
New-Item -ItemType Directory -Force -Path $modGateDocsDestination | Out-Null
foreach ($modGateGuide in @('PLAYER-GUIDE.md', 'SERVER-OWNER-GUIDE.md', 'TECHNICAL-REFERENCE.md')) {
    Copy-Item -LiteralPath (Join-Path $modGateProjectRoot "docs/$modGateGuide") -Destination $modGateDocsDestination -Force
}
Copy-Item -LiteralPath (Join-Path $modGateProjectRoot 'publishing/CHANGELOG.md') -Destination $modGateDist -Force
$modGateZip = Join-Path $modGateDist 'ValheimModGate-0.4.4.zip'
Compress-Archive -Path (Join-Path $modGateDist 'BepInEx'), (Join-Path $modGateDist 'README.md'), (Join-Path $modGateDist 'LICENSE'), (Join-Path $modGateDist 'server-mods.example.json'), $modGateDocsDestination, (Join-Path $modGateDist 'CHANGELOG.md') -DestinationPath $modGateZip -Force
Write-Output "Install package: $modGateZip"
