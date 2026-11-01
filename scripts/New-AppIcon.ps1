param([string]$PreviewPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'Compressr.App\Assets'
$null = [IO.Directory]::CreateDirectory($assets)
function Icon-Frame([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $draw = $visual.RenderOpen()
    try {
        $draw.PushTransform([Windows.Media.ScaleTransform]::new($size / 256.0, $size / 256.0))
        $navy = [Windows.Media.BrushConverter]::new().ConvertFromString('#183344')
        $teal = [Windows.Media.BrushConverter]::new().ConvertFromString('#006D77')
        $white = [Windows.Media.Brushes]::White
        $draw.DrawRoundedRectangle($navy, $null, [Windows.Rect]::new(12, 12, 232, 232), 44, 44)
        $draw.DrawEllipse($teal, [Windows.Media.Pen]::new($white, 10), [Windows.Point]::new(128, 128), 80, 80)
        $draw.DrawEllipse($navy, $null, [Windows.Point]::new(128, 128), 30, 30)
        $play = [Windows.Media.Geometry]::Parse('M 119,109 L 148,128 L 119,147 Z')
        $draw.DrawGeometry($white, $null, $play)
        $draw.Pop()
    } finally { $draw.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Icon-Frame $_) })
$stream = [IO.File]::Create((Join-Path $assets 'Compressr.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
if ($PreviewPath) { [IO.File]::WriteAllBytes($PreviewPath, $frames[-1]) }
Write-Host "Created original Compressr icon: $($sizes -join ', ') px"
