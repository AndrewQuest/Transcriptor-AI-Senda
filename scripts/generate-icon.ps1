Add-Type -AssemblyName System.Drawing
$destination = Join-Path $PSScriptRoot '../src/Transcriptor.App/Assets/Senda.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = @()
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([Drawing.Color]::Transparent)
    $graphics.ScaleTransform($size / 32.0, $size / 32.0)
    $background = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(214, 92, 66))
    $graphics.FillEllipse($background, 1, 1, 30, 30)
    $pen = [Drawing.Pen]::new([Drawing.Color]::White, 3)
    $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($pen, 10, 13, 10, 19)
    $graphics.DrawLine($pen, 16, 8, 16, 24)
    $graphics.DrawLine($pen, 22, 12, 22, 20)
    $stream = [IO.MemoryStream]::new()
    $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $frames += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path (Split-Path $destination) 'Senda.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $pen.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$writer = [IO.BinaryWriter]::new([IO.File]::Create($destination))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = $sizes[$index] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
