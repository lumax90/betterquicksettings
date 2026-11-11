param(
    [ValidateSet('run', 'build', 'check', 'publish', 'capture')]
    [string]$Task = 'run'
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Set-Location $PSScriptRoot
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($dotnet) {
    $executable = $dotnet.Source
} elseif ($env:DOTNET_ROOT -and (Test-Path "$env:DOTNET_ROOT\dotnet.exe")) {
    $executable = "$env:DOTNET_ROOT\dotnet.exe"
} else {
    throw '.NET 10 SDK is required: https://dotnet.microsoft.com/download/dotnet/10.0'
}
switch ($Task) {
    'run' { & $executable run --project BetterQuickSettings.csproj -c Release }
    'build' { & $executable build BetterQuickSettings.csproj -c Release --nologo }
    'check' { & $executable run --project checks/BetterQuickSettings.Checks.csproj -c Release }
    'publish' { & $executable publish BetterQuickSettings.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64 --nologo }
    'capture' {
        & $executable build BetterQuickSettings.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        & $executable 'bin/Release/net10.0-windows10.0.19041.0/BetterQuickSettings.dll' --capture (Join-Path $PSScriptRoot 'artifacts/panel.png')
    }
}
exit $LASTEXITCODE
