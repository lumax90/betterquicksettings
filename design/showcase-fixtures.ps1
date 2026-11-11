# Synthetic image fixtures for documentation. No desktop or user files are read.
param([Parameter(Mandatory)][string]$OutputDirectory)
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$scenes = @(
    @{ Name='Release notes'; Top='#37596D'; Bottom='#253851'; Kind='Notes' },
    @{ Name='Color study'; Top='#675376'; Bottom='#253B55'; Kind='Colors' },
    @{ Name='Mountain trail'; Top='#AAC8D5'; Bottom='#E5C4AF'; Kind='Landscape' },
    @{ Name='Project overview'; Top='#426878'; Bottom='#253A52'; Kind='Dashboard' },
    @{ Name='Weekend plans'; Top='#58705F'; Bottom='#273D40'; Kind='Notes' },
    @{ Name='Night palette'; Top='#3D426C'; Bottom='#172335'; Kind='Colors' }
)
function Brush([string]$color) { [System.Windows.Media.BrushConverter]::new().ConvertFromString($color) }
function Label($dc, [string]$text, [double]$x, [double]$y, [double]$size, [string]$color) {
    $formatted = [System.Windows.Media.FormattedText]::new($text, [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Windows.FlowDirection]::LeftToRight, [System.Windows.Media.Typeface]::new('Segoe UI'), $size, (Brush $color), 1.0)
    $dc.DrawText($formatted, [System.Windows.Point]::new($x, $y))
}
foreach ($scene in $scenes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $dc = $visual.RenderOpen()
    $gradient = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.ColorConverter]::ConvertFromString($scene.Top),
        [System.Windows.Media.ColorConverter]::ConvertFromString($scene.Bottom), 55)
    $dc.DrawRectangle($gradient, $null, [System.Windows.Rect]::new(0, 0, 960, 600))
    switch ($scene.Kind) {
        'Landscape' {
            $dc.DrawEllipse((Brush '#F3DFBD'), $null, [System.Windows.Point]::new(706, 158), 57, 57)
            foreach ($layer in @(
                @('#809AAA', 'M 0,450 L 220,148 L 408,365 L 594,180 L 960,432 L 960,600 L 0,600 Z'),
                @('#3D6C78', 'M 0,515 L 334,294 L 496,478 L 770,288 L 960,472 L 960,600 L 0,600 Z'),
                @('#25434F', 'M 0,550 L 194,438 L 372,540 L 558,422 L 960,530 L 960,600 L 0,600 Z')
            )) { $dc.DrawGeometry((Brush $layer[0]), $null, [System.Windows.Media.Geometry]::Parse($layer[1])) }
        }
        'Colors' {
            Label $dc $scene.Name 64 48 32 '#F3F3F8'
            $colors = if ($scene.Name -eq 'Night palette') { @('#D6CAF0','#A299D0','#776CA8','#4B527E','#B4C9D6') } else { @('#E8C4AF','#A6BEB5','#AAC7D8','#BEB0CF','#E2D5B2') }
            for ($i=0; $i -lt $colors.Length; $i++) {
                $dc.DrawRoundedRectangle((Brush $colors[$i]), $null, [System.Windows.Rect]::new((64+$i*169), 133, 152, 342), 20, 20)
                Label $dc $colors[$i] (67+$i*169) 500 20 '#D9E0EC'
            }
        }
        'Dashboard' {
            $dc.DrawRoundedRectangle((Brush '#E8EEF3'), $null, [System.Windows.Rect]::new(44, 44, 872, 512), 22, 22)
            Label $dc $scene.Name 78 75 31 '#233247'
            for ($i=0; $i -lt 3; $i++) {
                $dc.DrawRoundedRectangle((Brush '#CEDBE4'), $null, [System.Windows.Rect]::new((78+$i*278), 145, 251, 107), 14, 14)
                Label $dc @('24','08','96%')[$i] (102+$i*278) 164 37 '#314C66'
            }
            $pen = [System.Windows.Media.Pen]::new((Brush '#779CA5'), 7)
            $dc.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse('M 96,465 L 223,414 L 350,438 L 477,358 L 604,381 L 731,313 L 859,290'))
        }
        'Notes' {
            $dc.DrawRoundedRectangle((Brush '#F0F1EF'), $null, [System.Windows.Rect]::new(74, 54, 812, 492), 20, 20)
            Label $dc $scene.Name 116 92 36 '#2C4052'
            Label $dc 'A little space for the next idea.' 116 154 23 '#6C7F8B'
            for ($i=0; $i -lt 5; $i++) {
                $dc.DrawRoundedRectangle((Brush '#A6B5BB'), $null, [System.Windows.Rect]::new(118, (236+$i*48), (638-($i%3)*82), 9), 4, 4)
            }
        }
    }
    $dc.Close()
    $image = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(960,600,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($image))
    $path = Join-Path $OutputDirectory ($scene.Name.Replace(' ', '-').ToLowerInvariant()+'.png')
    $file = [System.IO.File]::Create($path)
    try { $encoder.Save($file) } finally { $file.Dispose() }
    [pscustomobject]@{ Name=$scene.Name; Uri=([Uri]([System.IO.Path]::GetFullPath($path))).AbsoluteUri }
}
