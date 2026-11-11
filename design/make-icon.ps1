$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$visual = New-Object System.Windows.Media.DrawingVisual
$drawing = $visual.RenderOpen()
$mint = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(199,224,206))
$ink = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(35,53,42))
$pen = New-Object System.Windows.Media.Pen($ink,2.2)
$pen.StartLineCap = 'Round'
$pen.EndLineCap = 'Round'
$drawing.DrawEllipse($mint,$null,(New-Object System.Windows.Point(16,16)),15,15)
foreach ($y in @(11,16,21)) { $drawing.DrawLine($pen,(New-Object System.Windows.Point(10,$y)),(New-Object System.Windows.Point(22,$y))) }
foreach ($point in @(@(15,11),@(20,16),@(13,21))) { $drawing.DrawEllipse($mint,$pen,(New-Object System.Windows.Point($point[0],$point[1])),2,2) }
$drawing.Close()
$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(32,32,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual)
$png = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$png.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$buffer = New-Object System.IO.MemoryStream
$png.Save($buffer)
$directory = Join-Path (Split-Path $PSScriptRoot) 'Assets'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$stream = [System.IO.File]::Create((Join-Path $directory 'app.ico'))
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
    $writer.Write([byte]32); $writer.Write([byte]32); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$buffer.Length); $writer.Write([uint32]22)
    $writer.Write($buffer.ToArray())
} finally { $writer.Dispose(); $buffer.Dispose() }
