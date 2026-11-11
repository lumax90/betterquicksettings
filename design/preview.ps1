param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/design'),
    [ValidateRange(310, 1400)][double]$PanelHeight = 360,
    [ValidateSet('Dark', 'Light')][string]$Backdrop = 'Dark',
    [ValidateRange(0, 100)][double]$Glass = 55,
    [switch]$Populated,
    [switch]$Showcase,
    [ValidateRange(1, 3)][int]$Scale = 1
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

# Interpret the actual XAML without building or launching the app. No HWND, hooks,
# device access, keyboard focus, or user data. The backdrop is a design swatch;
# Windows' live desktop acrylic can only be assessed in a running application.
$root = Split-Path $PSScriptRoot
[xml]$themeXml = [System.IO.File]::ReadAllText((Join-Path $root 'UI/Theme.xaml'))
# Motion runs in the native app. Static previews show the final states using standard WPF controls.
foreach ($node in @($themeXml.SelectNodes('//*[@Property]'))) {
    if ($node.GetAttribute('Property').StartsWith('motion:')) { $node.ParentNode.RemoveChild($node) | Out-Null }
}
foreach ($node in $themeXml.SelectNodes('//*')) {
    foreach ($attribute in @($node.Attributes)) {
        if ($attribute.Prefix -eq 'motion') { $node.RemoveAttribute($attribute.LocalName, $attribute.NamespaceURI) }
    }
}
$theme = [System.Windows.Markup.XamlReader]::Parse($themeXml.OuterXml)
$application = New-Object System.Windows.Application
$application.Resources.MergedDictionaries.Add($theme)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

[xml]$document = [System.IO.File]::ReadAllText((Join-Path $root 'MainWindow.xaml'))
$document.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($node in $document.SelectNodes('//*')) {
    foreach ($event in @('Click', 'Checked', 'Expanded', 'Collapsed', 'PreviewKeyDown', 'Deactivated', 'PreviewDragOver', 'PreviewDragLeave', 'PreviewDrop', 'PreviewMouseLeftButtonDown', 'PreviewMouseMove', 'PreviewMouseLeftButtonUp', 'KeyDown', 'SizeChanged', 'DragStarted', 'DragDelta', 'DragCompleted')) {
        $node.RemoveAttribute($event)
    }
}
$window = [System.Windows.Markup.XamlReader]::Parse($document.OuterXml)
$surface = $window.Content
$surface.Background = $theme['GlassTint']
$surface.CornerRadius = New-Object System.Windows.CornerRadius(8)
$surface.Height = $PanelHeight
$controls = $window.FindName('ControlsPage')
$notes = $window.FindName('NotesPage')
$preferences = $window.FindName('PreferencesPage')
$files = $window.FindName('FilesPage')
$filesTab = $window.FindName('FilesTab')
$history = $window.FindName('ClipboardPage')
$historyTab = $window.FindName('ClipboardTab')
$tools = $window.FindName('ToolsExpander')
$controlsTab = $window.FindName('ControlsTab')
$notesTab = $window.FindName('NotesTab')
$window.Content = $null
[System.Windows.Documents.TextElement]::SetFontFamily($surface, (New-Object System.Windows.Media.FontFamily('Segoe UI Variable Text, Segoe UI')))
$surface.UseLayoutRounding = $true
$fixturePath = Join-Path $OutputDirectory 'sample-capture.png'
$drawing = New-Object System.Windows.Media.DrawingVisual
$dc = $drawing.RenderOpen()
$dc.DrawRectangle((New-Object System.Windows.Media.LinearGradientBrush([System.Windows.Media.Colors]::CadetBlue, [System.Windows.Media.Colors]::MidnightBlue, 25)), $null, (New-Object System.Windows.Rect(0, 0, 1280, 800)))
$dc.DrawRoundedRectangle([System.Windows.Media.Brushes]::WhiteSmoke, $null, (New-Object System.Windows.Rect(110, 100, 1060, 600)), 22, 22)
$typeface = New-Object System.Windows.Media.Typeface('Segoe UI')
$heading = New-Object System.Windows.Media.FormattedText('Project notes', [System.Globalization.CultureInfo]::InvariantCulture, [System.Windows.FlowDirection]::LeftToRight, $typeface, 43, [System.Windows.Media.Brushes]::MidnightBlue, 1.0)
$dc.DrawText($heading, (New-Object System.Windows.Point(172, 155)))
foreach ($row in 0..5) {
    $dc.DrawRoundedRectangle([System.Windows.Media.Brushes]::LightSlateGray, $null, (New-Object System.Windows.Rect(174, (264 + $row * 52), (750 - ($row % 3) * 130), 12)), 6, 6)
}
$dc.Close()
$fixture = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(1280, 800, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$fixture.Render($drawing)
$fixtureEncoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$fixtureEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($fixture))
$fixtureFile = [System.IO.File]::Create($fixturePath)
try { $fixtureEncoder.Save($fixtureFile) } finally { $fixtureFile.Dispose() }
# URI fixtures avoid PowerShell's PSObject wrapper around BitmapSource values.
$sampleThumbnail = ([Uri]([System.IO.Path]::GetFullPath($fixturePath))).AbsoluteUri
$shotCount = if ($Populated) { 24 } else { 3 }
$sampleShots = @(1..$shotCount | ForEach-Object { [pscustomobject]@{
    Name = ('Screenshot 2026-11-10 124' + $_ + '00.png'); TimeLabel = ('12:4' + $_)
    Thumbnail = $sampleThumbnail; HasThumbnail = ($null -ne $sampleThumbnail); IsScreenshot = $true
} })
$sampleFiles = @(
    [pscustomobject]@{ Name = 'Project brief.pdf'; Details = 'PDF - 240 KB'; Glyph = [string][char]0xE8A5; Thumbnail = $null },
    [pscustomobject]@{ Name = 'Assets'; Details = 'Folder'; Glyph = [string][char]0xE8B7; Thumbnail = $null }
)
if ($Showcase) {
    $fixtures = @(& (Join-Path $PSScriptRoot 'showcase-fixtures.ps1') -OutputDirectory (Join-Path $OutputDirectory 'fixtures'))
    $sampleThumbnail = $fixtures[0].Uri
    $sampleShots = @(for ($i=0; $i -lt $fixtures.Count; $i++) { [pscustomobject]@{
        Name=$fixtures[$i].Name; TimeLabel=('14:' + (36-$i*3)); Thumbnail=$fixtures[$i].Uri; HasThumbnail=$true; IsScreenshot=$true
    } })
    $sampleFiles = @(
        [pscustomobject]@{Name='Project brief.pdf'; Details='PDF - 240 KB'; Glyph=[string][char]0xE8A5; Thumbnail=$null},
        [pscustomobject]@{Name='Brand assets'; Details='Folder'; Glyph=[string][char]0xE8B7; Thumbnail=$null},
        [pscustomobject]@{Name='Mountain trail.png'; Details='PNG - 1.2 MB'; Glyph=[string][char]0xEB9F; Thumbnail=$fixtures[2].Uri}
    )
}
$surface.DataContext = [pscustomobject]@{
    Volume = 35.0; Brightness = 72.0; AudioAvailable = $true; BrightnessAvailable = $true
    VolumeGlyph = [string][char]0xE767; OutputName = 'WH-1000XM5'; Outputs = @()
    AutoHeadphones = $true; ReplaceQuickSettings = $true; BrightnessDescription = ''
    Network = 'Wi-Fi'; Bluetooth = 'Bluetooth'; FocusLabel = '25 min'; FocusRunning = $false
    Note = ''; NoteCount = '0 characters'; NoteStatus = ''; Notice = ''; HasNotice = $false
    IsComposingNote = $true; IsEditingNote = $false; NoteReadOnly = $false; CanAddNote = $false; HasNotes = $true; EditingNoteLabel = ''
    Notes = @(
        [pscustomobject]@{Title='Ideas for the weekend'; Excerpt='Coffee, a long walk, and a little less screen time.'; ModifiedLabel='14:32'},
        [pscustomobject]@{Title='Send the final mockups'; Excerpt='Include the compact layout and the image shelf.'; ModifiedLabel='13:05'}
    )
    ToolsExpanded = $false; HasScreenshots = $true; HasHeldFiles = $true; AddingFiles = $false; FileDragOver = $false
    RecentScreenshots = $sampleShots; HeldFiles = $sampleFiles; LatestScreenshot = $sampleShots[0]; LatestScreenshotPreview = $sampleThumbnail; FileCount = [string]($sampleShots.Count + $sampleFiles.Count)
    ScreenshotFolderLabel = 'Windows Screenshots'
    ClipboardHistoryEnabled = $true; VolumeFeedbackEnabled = $true
    ClipboardSummary = '4 / 50'; ClipboardSearch = ''; HasClipboardItems = $true; HasClipboardResults = $true; ClipboardEmptyMessage = ''
    ClipboardItems = @(
        [pscustomobject]@{Preview='The little things make a big difference.'; Details='Text - 40 characters'; TimeLabel='14:36'; CopyLabel='Copy to clipboard'; IsImage=$false; IsText=$true; Thumbnail=$null},
        [pscustomobject]@{Preview='https://github.com'; Details='Text - 18 characters'; TimeLabel='14:30'; CopyLabel='Copy to clipboard'; IsImage=$false; IsText=$true; Thumbnail=$null},
        [pscustomobject]@{Preview='Image'; Details='Image - 1920 x 1080'; TimeLabel='14:25'; CopyLabel='Copy to clipboard'; IsImage=$true; IsText=$false; Thumbnail=$sampleThumbnail},
        [pscustomobject]@{Preview='Meeting notes: review the final layout and share the updated assets.'; Details='Text - 66 characters'; TimeLabel='13:58'; CopyLabel='Copy to clipboard'; IsImage=$false; IsText=$true; Thumbnail=$null}
    )
    GlassAmount = $Glass; GlassLabel = $(if ($Glass -eq 0) { 'Off' } else { "$Glass%" }); PanelHeight = $PanelHeight
    IntegrationStatus = ''; PlayGlyph = [string][char]0xE769
    Media = [pscustomobject]@{
        Available = $true; Title = 'Midnight City'; Artist = 'M83'; Source = 'Spotify'
        CanPrevious = $true; CanPlay = $true; CanNext = $true; Artwork = $null
    }
}
if ($Showcase) {
    $surface.DataContext.Note = 'Save the idea now. Come back to the details later.'
    $surface.DataContext.NoteCount = ($surface.DataContext.Note.Length.ToString() + ' characters')
    $surface.DataContext.CanAddNote = $true
    $surface.DataContext.Notes = @(
        [pscustomobject]@{Title='Release checklist'; Excerpt='Update the docs, collect screenshots, ship the build.'; ModifiedLabel='14:32'},
        [pscustomobject]@{Title='Weekend plans'; Excerpt='Coffee, a mountain trail, and a camera.'; ModifiedLabel='13:05'},
        [pscustomobject]@{Title='Small improvements'; Excerpt='Less clicking. More room for the things I use.'; ModifiedLabel='Nov 10'}
    )
    $surface.DataContext.Outputs = @(
        [pscustomobject]@{Name='WH-1000XM5'; IsDefault=$true},
        [pscustomobject]@{Name='Speakers'; IsDefault=$false}
    )
    $surface.DataContext.ClipboardItems = @(
        [pscustomobject]@{Preview='https://github.com/lumax90/betterquicksettings'; Details='Text'; TimeLabel='14:36'; CopyLabel='Copy to clipboard'; IsImage=$false; IsText=$true; Thumbnail=$null},
        [pscustomobject]@{Preview='Image'; Details='Image - 960 x 600'; TimeLabel='14:30'; CopyLabel='Copy to clipboard'; IsImage=$true; IsText=$false; Thumbnail=$fixtures[2].Uri},
        [pscustomobject]@{Preview='Remember the useful things. Keep the rest out of the way.'; Details='Text - 56 characters'; TimeLabel='14:25'; CopyLabel='Copy to clipboard'; IsImage=$false; IsText=$true; Thumbnail=$null},
        [pscustomobject]@{Preview='Color study'; Details='Image - 960 x 600'; TimeLabel='13:58'; CopyLabel='Copy to clipboard'; IsImage=$true; IsText=$false; Thumbnail=$fixtures[1].Uri}
    )
}

$hostPanel = New-Object System.Windows.Controls.Grid
$hostPanel.Width = $window.Width + 32
$hostPanel.UseLayoutRounding = $true
$background = New-Object System.Windows.Media.LinearGradientBrush
$background.StartPoint = New-Object System.Windows.Point(0, 0)
$background.EndPoint = New-Object System.Windows.Point(1, 1)
$background.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString('#526775'), 0)))
$background.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString('#333F56'), 0.5)))
$background.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.ColorConverter]::ConvertFromString('#56516A'), 1)))
$hostPanel.Background = $background
if ($Backdrop -eq 'Light') { $hostPanel.Background = [System.Windows.Media.Brushes]::WhiteSmoke }

function Tint([double]$alpha, [byte]$r, [byte]$g, [byte]$b) {
    [System.Windows.Media.Color]::FromArgb([byte][math]::Round([math]::Max(0.0, [math]::Min(1.0, $alpha)) * 255), $r, $g, $b)
}
function Apply-Swatch($target) {
    $strength = $Glass / 100
    $clarity = [math]::Pow($strength, 1.25)
    $opacity = if ($Glass -eq 0) { 1 } else { 0.93 * [math]::Pow(1 - $strength, 1.3) + 0.025 * $strength }
    $backgroundLevel = if ($Backdrop -eq 'Light') { 0.96 } else { 0.27 }
    $light = $Glass -gt 0 -and ($backgroundLevel * (1 - $opacity) + 0.11 * $opacity) -gt 0.58
    $tintTop = Tint $opacity 31 36 44
    $tintMiddle = Tint ($opacity * 0.85) 21 26 33
    $tintBottom = Tint $opacity 32 37 46
    if ($light) {
        $cellTop = Tint 0.18 255 255 255
        $cellBottom = Tint 0.10 255 255 255
        $header = Tint 0.32 255 255 255
        $ink = '#161C26'; $muted = '#374150'; $accent = '#23497B'; $fillTop = '#43536A'; $fillBottom = '#24334A'
    } else {
        $cellTop = Tint (0.24 - $clarity * 0.08) 15 19 26
        $cellBottom = Tint (0.30 - $clarity * 0.08) 15 19 26
        $header = Tint (0.38 - $clarity * 0.10) 13 18 25
        $ink = '#F7F9FD'; $muted = '#D5DDE8'; $accent = '#C0D8F9'; $fillTop = '#EEF5FF'; $fillBottom = '#BCCFEA'
    }
    # Parse resources as native WPF objects, avoiding PSObject-wrapped brush values.
    $swatch = [System.Windows.Markup.XamlReader]::Parse(@"
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <LinearGradientBrush x:Key="GlassTint" StartPoint="0,0" EndPoint="0,1"><GradientStop Color="$tintTop" Offset="0" /><GradientStop Color="$tintMiddle" Offset="0.55" /><GradientStop Color="$tintBottom" Offset="1" /></LinearGradientBrush>
  <LinearGradientBrush x:Key="GlassCell" StartPoint="0,0" EndPoint="0.2,1"><GradientStop Color="$cellTop" Offset="0" /><GradientStop Color="$cellBottom" Offset="1" /></LinearGradientBrush>
  <SolidColorBrush x:Key="GlassHeader" Color="$header" />
  <SolidColorBrush x:Key="Ink" Color="$ink" /><SolidColorBrush x:Key="Muted" Color="$muted" /><SolidColorBrush x:Key="Accent" Color="$accent" />
  <LinearGradientBrush x:Key="SliderFill" StartPoint="0,0" EndPoint="0,1"><GradientStop Color="$fillTop" Offset="0" /><GradientStop Color="$fillBottom" Offset="1" /></LinearGradientBrush>
</ResourceDictionary>
"@)
    $target.Resources.MergedDictionaries.Add($swatch)
    $target.Background = if ($Glass -eq 0) { $theme['GlassFallback'] } else { $swatch['GlassTint'] }
}
Apply-Swatch $surface
$surface.Margin = New-Object System.Windows.Thickness(16)
$surface.HorizontalAlignment = 'Stretch'
$hostPanel.Children.Add($surface) | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

function Capture-Source([string]$name) {
    $hostPanel.Measure((New-Object System.Windows.Size($hostPanel.Width, [double]::PositiveInfinity)))
    $hostPanel.Arrange((New-Object System.Windows.Rect(0, 0, $hostPanel.Width, $hostPanel.DesiredSize.Height)))
    $hostPanel.UpdateLayout()
    $preview = $window.FindName('LatestScreenshotPreviewHost')
    if ($preview -and $preview.ActualWidth -gt 0 -and $preview.ActualHeight -gt 0) {
        $preview.Clip = New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(0, 0, $preview.ActualWidth, $preview.ActualHeight)), 11, 11)
    }
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::Render)
    $hostPanel.UpdateLayout()
    $width = [int][math]::Ceiling($hostPanel.ActualWidth)
    $height = [int][math]::Ceiling($hostPanel.ActualHeight)
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(($width * $Scale), ($height * $Scale), (96 * $Scale), (96 * $Scale), [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($hostPanel)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $path = Join-Path $OutputDirectory ($name + '.png')
    $stream = [System.IO.File]::Create($path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    [pscustomobject]@{ View = $name; PanelWidth = $width - 32; PanelHeight = $height - 32; LatestPreviewHeight = [math]::Round($preview.ActualHeight, 1); ControlsExtent = [math]::Round($controls.ExtentHeight, 1); ControlsOffset = [math]::Round($controls.VerticalOffset, 1); FilesViewportWidth = $files.ViewportWidth; Image = [System.IO.Path]::GetFullPath($path) }
}

Capture-Source 'controls'
if ($Showcase) {
    $window.FindName('LatestScreenshotPreviewHost').Visibility = 'Collapsed'
    $window.FindName('OutputPicker').Visibility = 'Visible'
    Capture-Source 'audio-output'
    $window.FindName('OutputPicker').Visibility = 'Collapsed'
    $window.FindName('LatestScreenshotPreviewHost').Visibility = 'Visible'
}
$tools.IsExpanded = $true
Capture-Source 'controls-expanded'
$controls.ScrollToBottom()
Capture-Source 'controls-expanded-bottom'
$controls.ScrollToTop()
$tools.IsExpanded = $false
$controls.Visibility = 'Collapsed'
$files.Visibility = 'Visible'
$controlsTab.IsChecked = $false
$filesTab.IsChecked = $true
Capture-Source 'files'
$files.Visibility = 'Collapsed'
$filesTab.IsChecked = $false
$notes.Visibility = 'Visible'
$controlsTab.IsChecked = $false
$notesTab.IsChecked = $true
Capture-Source 'notes'
$notebook = $surface.DataContext
$editor = $notebook.PSObject.Copy()
$editor.IsComposingNote = $false
$editor.IsEditingNote = $true
$editor.Note = "Project notes`r`n`r`n" + ((1..18 | ForEach-Object { 'Keep the layout readable at every panel height.' }) -join "`r`n")
$editor.NoteStatus = 'Saved'
$editor.EditingNoteLabel = '14:32'
$surface.DataContext = $editor
Capture-Source 'note-editor'
$surface.DataContext = $notebook
$notes.Visibility = 'Collapsed'
$notesTab.IsChecked = $false
$history.Visibility = 'Visible'
$historyTab.IsChecked = $true
Capture-Source 'history'
$history.Visibility = 'Collapsed'
$historyTab.IsChecked = $false
$preferences.Visibility = 'Visible'
Capture-Source 'preferences'
[xml]$hintXml = [System.IO.File]::ReadAllText((Join-Path $root 'UI/DropHintWindow.xaml'))
$hintXml.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
foreach ($event in @('DragOver', 'DragLeave', 'Drop')) { $hintXml.DocumentElement.RemoveAttribute($event) }
$hintWindow = [System.Windows.Markup.XamlReader]::Parse($hintXml.OuterXml)
$hintSurface = $hintWindow.Content
$hintSurface.Background = $theme['GlassTint']
$hintSurface.CornerRadius = New-Object System.Windows.CornerRadius(8)
Apply-Swatch $hintSurface
$hintWindow.Content = $null
$hostPanel.Children.Clear()
$hostPanel.Width = $hintWindow.Width + 32
$hintSurface.Margin = New-Object System.Windows.Thickness(16)
$hintSurface.Height = $hintWindow.Height
[System.Windows.Documents.TextElement]::SetFontFamily($hintSurface, (New-Object System.Windows.Media.FontFamily('Segoe UI Variable Text, Segoe UI')))
$hostPanel.Children.Add($hintSurface) | Out-Null
Capture-Source 'drop-target'
if ($Showcase) {
    $hostPanel.Children.Clear()
    $scene = [System.Windows.Markup.XamlReader]::Parse([System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'drop-scene.xaml')))
    $hintSurface.Margin = [System.Windows.Thickness]::new(0)
    $scene.FindName('DropTargetHost').Child = $hintSurface
    [System.Windows.Documents.TextElement]::SetFontFamily($scene, [System.Windows.Media.FontFamily]::new('Segoe UI Variable Text, Segoe UI'))
    $scene.Measure([System.Windows.Size]::new($scene.Width, $scene.Height))
    $scene.Arrange([System.Windows.Rect]::new(0,0,$scene.Width,$scene.Height))
    $scene.UpdateLayout()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new([int]($scene.Width*$Scale),[int]($scene.Height*$Scale),96*$Scale,96*$Scale,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($scene)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file = [System.IO.File]::Create((Join-Path $OutputDirectory 'drop-to-shelf.png'))
    try { $encoder.Save($file) } finally { $file.Dispose() }
}
$hintWindow.Close()
$window.Close()
