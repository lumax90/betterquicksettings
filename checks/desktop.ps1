param([string]$Executable = (Join-Path $PSScriptRoot '../bin/Release/net10.0-windows10.0.19041.0/BetterQuickSettings.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class QuickSettingsDesktopCheck {
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 public struct Point { public int X,Y; }
 public static void Key(byte key,bool up=false) { keybd_event(key,0,up?2u:0u,UIntPtr.Zero); }
 public static void Shortcut() { Key(0x5B); System.Threading.Thread.Sleep(60); Key(0x41); System.Threading.Thread.Sleep(60); Key(0x41,true); System.Threading.Thread.Sleep(60); Key(0x5B,true); }
 public static void Escape() { Key(0x1B); System.Threading.Thread.Sleep(60); Key(0x1B,true); }
 public static void Click(int x,int y) { SetCursorPos(x,y); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
}
'@
$title = 'BetterQuickSettings'
if ([QuickSettingsDesktopCheck]::FindWindow([NullString]::Value, $title) -ne [IntPtr]::Zero) { throw 'Close the running application before running desktop checks.' }
$directory = Join-Path $PSScriptRoot ('../artifacts/desktop-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$process = $null
$report = [System.Collections.Generic.List[string]]::new()
function Wait-For([scriptblock]$condition, [string]$failure) {
    $until = [DateTime]::UtcNow.AddSeconds(12)
    do {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw $failure
}
function Panel {
    $handle = [QuickSettingsDesktopCheck]::FindWindow([NullString]::Value, $title)
    if ($handle -eq [IntPtr]::Zero) { return $null }
    return [System.Windows.Automation.AutomationElement]::FromHandle($handle)
}
function Visible {
    $handle = [QuickSettingsDesktopCheck]::FindWindow([NullString]::Value, $title)
    return $handle -ne [IntPtr]::Zero -and [QuickSettingsDesktopCheck]::IsWindowVisible($handle)
}
function Find-Id([string]$id) {
    return (Panel).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function Select-Tab([string]$id) {
    $control = Find-Id $id
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}
function Start-QuickSettings {
    $script:process = Start-Process -FilePath $Executable -ArgumentList @('--background', '--data-dir', ('"' + $directory + '"')) -PassThru
    Wait-For { [QuickSettingsDesktopCheck]::FindWindow([NullString]::Value, $title) -ne [IntPtr]::Zero } 'BetterQuickSettings did not initialize.'
    Start-Sleep -Milliseconds 1400
}
function Stop-QuickSettings {
    if (-not (Visible)) { [QuickSettingsDesktopCheck]::Shortcut(); Wait-For { Visible } 'Cannot show the panel to quit.' }
    $settings = Find-Id 'PreferencesButton'
    $settings.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 150
    $quit = Find-Id 'QuitButton'
    $quit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $script:process.WaitForExit(5000)) { throw 'BetterQuickSettings did not exit cleanly.' }
}
try {
    Start-QuickSettings
    [QuickSettingsDesktopCheck]::Shortcut()
    Wait-For { Visible } 'Win+A did not open the panel.'
    $report.Add('PASS Win+A opens the panel')
    Select-Tab 'NotesTab'
    Wait-For { $null -ne (Find-Id 'NoteComposer') } 'Notes composer did not become visible.'
    $note = Find-Id 'NoteComposer'
    if ($null -eq $note) {
        Write-Output ('Panel visible after note selection: ' + (Visible))
        (Panel).FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current | Select-Object Name, AutomationId, IsOffscreen }
        throw 'Note editor lookup returned null.'
    }
    $text = 'BetterQuickSettings desktop persistence check ' + [guid]::NewGuid().ToString('N')
    $note.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
    Wait-For { (Test-Path (Join-Path $directory 'notes.json')) -and ((Get-Content (Join-Path $directory 'notes.json') -Raw | ConvertFrom-Json).Draft -eq $text) } 'Auto-save did not persist the draft.'
    $report.Add('PASS real UI note auto-save')
    [QuickSettingsDesktopCheck]::Escape()
    Wait-For { -not (Visible) } 'Escape did not dismiss the panel.'
    $report.Add('PASS Escape dismisses the panel')
    Start-Sleep -Milliseconds 250
    $trayHandle = [QuickSettingsDesktopCheck]::FindWindow('Shell_TrayWnd', [NullString]::Value)
    $tray = [System.Windows.Automation.AutomationElement]::FromHandle($trayHandle)
    $audio = $tray.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'SystemTray.OmniButtonRight')))
    if (-not $audio) { throw 'Expected system volume hit target was not found on this Windows build.' }
    $bounds = $audio.Current.BoundingRectangle
    $cursor = New-Object QuickSettingsDesktopCheck+Point
    [QuickSettingsDesktopCheck]::GetCursorPos([ref]$cursor) | Out-Null
    try {
        [QuickSettingsDesktopCheck]::Click([int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2))
        Wait-For { Visible } 'Clicking the Windows sound icon did not open the panel.'
        $report.Add('PASS existing Windows sound icon opens the panel')
        Start-Sleep -Milliseconds 300
        [QuickSettingsDesktopCheck]::Click([int]($bounds.X + $bounds.Width / 2), [int]($bounds.Y + $bounds.Height / 2))
        Wait-For { -not (Visible) } 'Second taskbar click did not close the panel.'
        $report.Add('PASS second taskbar click closes the panel')
    } finally { [QuickSettingsDesktopCheck]::SetCursorPos($cursor.X, $cursor.Y) | Out-Null }
    $process.Refresh()
    $cpu = $process.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 3
    $process.Refresh()
    $report.Add(('INFO hidden working set {0:N1} MB; CPU over 3 seconds {1:N1} ms' -f ($process.WorkingSet64 / 1MB), ($process.TotalProcessorTime.TotalMilliseconds - $cpu)))
    Stop-QuickSettings
    Start-QuickSettings
    [QuickSettingsDesktopCheck]::Shortcut()
    Wait-For { Visible } 'Second launch failed.'
    Select-Tab 'NotesTab'
    Wait-For { $null -ne (Find-Id 'NoteComposer') } 'Notes composer did not become visible after restart.'
    $restored = (Find-Id 'NoteComposer').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($restored -ne $text) { throw 'Note did not survive restart.' }
    $report.Add('PASS note survives full application restart')
    Stop-QuickSettings
    $report.Add('PASS graceful shutdown removes app and listeners')
    $report | Set-Content (Join-Path $directory 'report.txt')
    $report
    Write-Output ('Report: ' + $directory)
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
}
