param(
  [string]$InputPng = "e:\PackageManager\Assets\Icons\source.png",
  [string]$OutputIco = "e:\PackageManager\Assets\Icons\App.ico",
  [int[]]$Sizes = @(256,128,64,48,32,24,16)
)

Write-Host "Input: $InputPng" -ForegroundColor Cyan
Write-Host "Output: $OutputIco" -ForegroundColor Cyan
Write-Host "Sizes: $($Sizes -join ', ')" -ForegroundColor Cyan

if (!(Test-Path -LiteralPath $InputPng)) {
  Write-Error "Input PNG not found: $InputPng"
  exit 1
}

Add-Type -AssemblyName System.Drawing

function Resize-PngBytes {
  param(
    [System.Drawing.Image]$Image,
    [int]$Size
  )
  $dest = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($dest)
  $g.Clear([System.Drawing.Color]::Transparent)
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $ratio = [Math]::Min($Size / $Image.Width, $Size / $Image.Height)
  $nw = [int]([Math]::Round($Image.Width * $ratio))
  $nh = [int]([Math]::Round($Image.Height * $ratio))
  $dx = [int](($Size - $nw) / 2)
  $dy = [int](($Size - $nh) / 2)
  $rect = New-Object System.Drawing.Rectangle($dx, $dy, $nw, $nh)
  $g.DrawImage($Image, $rect)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $dest.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bytes = $ms.ToArray()
  $ms.Dispose()
  $dest.Dispose()
  return ,$bytes
}

function Write-IcoWithPng {
  param(
    [byte[][]]$PngImages,
    [int[]]$Sizes,
    [string]$OutputPath
  )
  $dir = Split-Path -Parent $OutputPath
  if (!(Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
  $fs = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create)
  $bw = New-Object System.IO.BinaryWriter($fs)
  # ICONDIR
  $bw.Write([UInt16]0)                  # reserved
  $bw.Write([UInt16]1)                  # type: 1=icon
  $bw.Write([UInt16]$PngImages.Count)   # count

  $offset = 6 + (16 * $PngImages.Count)
  for ($i = 0; $i -lt $PngImages.Count; $i++) {
    $size = $Sizes[$i]
    $data = $PngImages[$i]
    if ($size -gt 255) {
      $w = [byte]0
      $h = [byte]0
    } else {
      $w = [byte]$size
      $h = [byte]$size
    }
    $bw.Write([Byte]$w)                 # width
    $bw.Write([Byte]$h)                 # height
    $bw.Write([Byte]0)                  # color count
    $bw.Write([Byte]0)                  # reserved
    $bw.Write([UInt16]1)                # planes
    $bw.Write([UInt16]32)               # bit count
    $bw.Write([UInt32]$data.Length)     # bytes in res
    $bw.Write([UInt32]$offset)          # offset
    $offset += $data.Length
  }

  foreach ($data in $PngImages) { $bw.Write($data) }
  $bw.Close(); $fs.Close()
}

try {
  $img = [System.Drawing.Image]::FromFile($InputPng)
  $pngs = @()
  foreach ($s in $Sizes) { $pngs += ,(Resize-PngBytes -Image $img -Size $s) }
  $img.Dispose()
  Write-IcoWithPng -PngImages $pngs -Sizes $Sizes -OutputPath $OutputIco
  Write-Host "Icon generated: $OutputIco" -ForegroundColor Green
  exit 0
}
catch {
  Write-Error $_
  exit 1
}
