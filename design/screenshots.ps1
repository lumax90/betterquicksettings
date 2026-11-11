param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../docs/images'))
$ErrorActionPreference = 'Stop'
$temporary = Join-Path $PSScriptRoot '../artifacts/readme-gallery'
& powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'preview.ps1') -OutputDirectory $temporary -PanelHeight 490 -Showcase -Scale 2
if ($LASTEXITCODE -ne 0) { throw 'Could not render the documentation gallery.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($name in @('files', 'notes', 'history', 'audio-output', 'drop-to-shelf')) {
    Copy-Item -LiteralPath (Join-Path $temporary ($name + '.png')) -Destination (Join-Path $OutputDirectory ($name + '.png')) -Force
}
