param(
    [string]$IsccPath,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../artifacts/releases')
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path $PSScriptRoot
[xml]$project = [System.IO.File]::ReadAllText((Join-Path $root 'BetterQuickSettings.csproj'))
$version = [string]$project.Project.PropertyGroup.Version
$fileVersion = [string]$project.Project.PropertyGroup.FileVersion
if ($version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$' -or $fileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'The project must specify valid Version and FileVersion values.'
}
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($dotnet) { $dotnet = $dotnet.Source }
elseif ($env:DOTNET_ROOT -and (Test-Path "$env:DOTNET_ROOT\dotnet.exe")) { $dotnet = "$env:DOTNET_ROOT\dotnet.exe" }
else { throw 'Install the .NET 10 SDK or set DOTNET_ROOT.' }

if (-not $IsccPath) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($iscc) { $IsccPath = $iscc.Source }
    else {
        $locations = @(
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
        )
        $IsccPath = $locations | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath)) { throw 'Install Inno Setup 7 or supply -IsccPath.' }
$IsccPath = [System.IO.Path]::GetFullPath($IsccPath)
$release = [System.IO.Path]::GetFullPath((Join-Path $OutputRoot $version))
if (Test-Path -LiteralPath $release) { throw "Output already exists: $release. Use a fresh OutputRoot; published packages are never overwritten." }
$payload = Join-Path $release 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null

& $dotnet publish (Join-Path $root 'BetterQuickSettings.csproj') -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -o $payload --nologo
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
foreach ($required in @('BetterQuickSettings.exe','BetterQuickSettings.dll','BetterQuickSettings.runtimeconfig.json',
    'coreclr.dll','PresentationFramework.dll','Assets\app.ico','LICENSE','THIRD_PARTY_NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing release file: $required" }
}
$metadata = (Get-Item -LiteralPath (Join-Path $payload 'BetterQuickSettings.exe')).VersionInfo
if (-not $metadata.ProductVersion.StartsWith($version)) { throw 'Published executable version does not match the project.' }

# README assets are synthetic documentation images, not user captures.
Copy-Item -LiteralPath (Join-Path $root 'CONTRIBUTING.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $payload -Recurse
Copy-Item -LiteralPath (Join-Path $root 'installer\beta-notice.txt') -Destination (Join-Path $payload 'BETA-NOTICE.txt')
$instructions = @"
BetterQuickSettings $version - Windows 11 x64

Extract the entire archive before opening BetterQuickSettings.exe.
Keep all files together. The .NET runtime is included; no separate install is needed.
Use Win + A or the network/volume area to open the panel. Exit through the tray menu.

Portable means no installation is required. User data is saved under Local AppData,
not beside the executable. See README.md for data locations and compatibility notes.
Read BETA-NOTICE.txt before using this early release.
"@
[System.IO.File]::WriteAllText((Join-Path $payload 'START-HERE.txt'), $instructions)

$prefix = "BetterQuickSettings-$version-win-x64"
$portable = Join-Path $release "$prefix-Portable.zip"
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $portable -CompressionLevel Optimal
& $IsccPath "/DAppVersion=$version" "/DFileVersion=$fileVersion" "/DPublishDir=$payload" "/DReleaseDir=$release" `
    (Join-Path $root 'installer\BetterQuickSettings.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $release "$prefix-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw 'The expected Setup executable was not created.' }
$checksums = foreach ($file in @($setup, $portable)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($file))"
}
[System.IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $checksums, [System.Text.Encoding]::ASCII)
[pscustomobject]@{ Version=$version; Setup=$setup; Portable=$portable; Checksums=(Join-Path $release 'SHA256SUMS.txt') }
