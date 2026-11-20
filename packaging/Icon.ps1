param([Parameter(Mandatory=$true)][string]$OutputFolder,[switch]$Installer)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$art=[Drawing.Image]::FromFile((Join-Path $PSScriptRoot "../assets/ostum.png"))
$frames = @()
$sizes=if($Installer){@(16,32,48,64,128,256)}else{@(16,32,48,64,128,256)}
foreach ($size in $sizes) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.DrawImage($art,0,0,$size,$size)
    $memory = [IO.MemoryStream]::new()
    $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
    $frames += ,($memory.ToArray())
    if ($size -eq 128 -and !$Installer) { [IO.File]::WriteAllText((Join-Path $OutputFolder 'icon.b64'),[Convert]::ToBase64String($memory.ToArray())) }
    $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$art.Dispose()
$name=if($Installer){'setup.ico'}else{'ostum.ico'}
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $OutputFolder $name)))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($i=0; $i -lt $frames.Count; $i++) {
        $writer.Write([byte]($sizes[$i]%256)); $writer.Write([byte]($sizes[$i]%256)); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([int]$frames[$i].Length); $writer.Write([int]$offset); $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
