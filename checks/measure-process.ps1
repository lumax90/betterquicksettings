param(
    [int]$ProcessId,
    [ValidateRange(5, 120)][int]$Seconds = 24,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/performance.json')
)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class QuickSettingsProcessMeasurement {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c,string t);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@
$process = Get-Process -Id $ProcessId
$title = 'BetterQuickSettings'
$handle = [QuickSettingsProcessMeasurement]::FindWindow([NullString]::Value, $title)
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$previousCpu = $process.TotalProcessorTime.TotalMilliseconds
$previousTime = $clock.Elapsed.TotalMilliseconds
$rows = for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Milliseconds 1000
    $process.Refresh()
    $cpu = $process.TotalProcessorTime.TotalMilliseconds
    $now = $clock.Elapsed.TotalMilliseconds
    $core = ($cpu - $previousCpu) / ($now - $previousTime) * 100
    [pscustomobject]@{
        Sample = $i + 1
        CpuMs = [math]::Round($cpu - $previousCpu, 3)
        CorePercent = [math]::Round($core, 3)
        MachinePercent = [math]::Round($core / [Environment]::ProcessorCount, 3)
        WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 1)
        PrivateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        PanelVisible = [QuickSettingsProcessMeasurement]::IsWindowVisible($handle)
    }
    $previousCpu = $cpu
    $previousTime = $now
}
$rows | ConvertTo-Json | Set-Content $OutputPath
$rows | Format-Table -AutoSize
