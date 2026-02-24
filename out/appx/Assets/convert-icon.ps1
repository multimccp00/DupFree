# Convert logo.png to app.ico using System.Drawing
Add-Type -AssemblyName System.Drawing

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pngPath = Join-Path $scriptDir "dupfree_logo.png"
$icoPath = Join-Path $scriptDir "app.ico"

if (-not (Test-Path $pngPath)) {
    Write-Host "logo.png not found in Assets folder. Please save the logo image there first."
    exit 1
}

$img = [System.Drawing.Image]::FromFile($pngPath)

# Create multi-size ICO
$ms = New-Object System.IO.MemoryStream

# ICO header
$bw = New-Object System.IO.BinaryWriter($ms)
$sizes = @(16, 32, 48, 256)
$bw.Write([int16]0)       # Reserved
$bw.Write([int16]1)       # Type: ICO
$bw.Write([int16]$sizes.Count)  # Number of images

# Prepare PNG data for each size
$pngDataList = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($img, $size, $size)
    $pngStream = New-Object System.IO.MemoryStream
    $bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDataList += ,$pngStream.ToArray()
    $pngStream.Dispose()
    $bmp.Dispose()
}

# Write directory entries
$dataOffset = 6 + ($sizes.Count * 16)  # header + entries
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $data = $pngDataList[$i]
    $w = if ($size -ge 256) { 0 } else { $size }
    $h = $w
    $bw.Write([byte]$w)           # Width
    $bw.Write([byte]$h)           # Height
    $bw.Write([byte]0)            # Color palette
    $bw.Write([byte]0)            # Reserved
    $bw.Write([int16]1)           # Color planes
    $bw.Write([int16]32)          # Bits per pixel
    $bw.Write([int32]$data.Length) # Size of image data
    $bw.Write([int32]$dataOffset)  # Offset
    $dataOffset += $data.Length
}

# Write image data
foreach ($data in $pngDataList) {
    $bw.Write($data)
}

[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()
$img.Dispose()

Write-Host "Created app.ico with sizes: $($sizes -join ', ')px"
