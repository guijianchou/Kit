Add-Type -AssemblyName System.Drawing

function Convert-PngToWin32DibIco {
    param(
        [Parameter(Mandatory=$true)]
        [string]$SourcePngPath,
        [Parameter(Mandatory=$true)]
        [string]$TargetIcoPath,
        [int[]]$Sizes = @(128, 64, 48, 32, 24, 20, 16)
    )

    if (-not (Test-Path -LiteralPath $SourcePngPath)) {
        throw "Source PNG does not exist: $SourcePngPath"
    }

    $src = [System.Drawing.Image]::FromFile($SourcePngPath)
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    $frameSizes = [System.Collections.Generic.List[int]]::new()

    foreach ($size in $Sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.DrawImage($src, 0, 0, $size, $size)
        $g.Dispose()

        $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
        $bmpData = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $stride = [Math]::Abs($bmpData.Stride)
        $totalBytes = $stride * $size
        $rawPixels = New-Object byte[] $totalBytes
        [System.Runtime.InteropServices.Marshal]::Copy($bmpData.Scan0, $rawPixels, 0, $totalBytes)
        $bmp.UnlockBits($bmpData)
        $bmp.Dispose()

        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter $ms

        # BITMAPINFOHEADER (40 bytes)
        $bw.Write([uint32]40)
        $bw.Write([int32]$size)
        $bw.Write([int32]($size * 2)) # Double height for XOR + AND
        $bw.Write([uint16]1) # Planes
        $bw.Write([uint16]32) # BitCount
        $bw.Write([uint32]0) # Compression = BI_RGB
        $bw.Write([uint32]0) # ImageSize
        $bw.Write([int32]0) # X pixels per meter
        $bw.Write([int32]0) # Y pixels per meter
        $bw.Write([uint32]0) # ColorsUsed
        $bw.Write([uint32]0) # ColorsImportant

        # XOR mask (BGRA, bottom-up)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $rowOffset = $y * $stride
            for ($x = 0; $x -lt $size; $x++) {
                $pxOffset = $rowOffset + ($x * 4)
                $bw.Write([byte]$rawPixels[$pxOffset])
                $bw.Write([byte]$rawPixels[$pxOffset + 1])
                $bw.Write([byte]$rawPixels[$pxOffset + 2])
                $bw.Write([byte]$rawPixels[$pxOffset + 3])
            }
        }

        # AND mask (1bpp monochrome, bottom-up, row padded to 4-byte boundary)
        $maskRowStride = [int][Math]::Ceiling($size / 32.0) * 4
        $andMaskRow = New-Object byte[] $maskRowStride
        for ($y = $size - 1; $y -ge 0; $y--) {
            [Array]::Clear($andMaskRow, 0, $maskRowStride)
            $rowOffset = $y * $stride
            for ($x = 0; $x -lt $size; $x++) {
                $pxOffset = $rowOffset + ($x * 4)
                $alpha = $rawPixels[$pxOffset + 3]
                if ($alpha -eq 0) {
                    $byteIdx = [Math]::Floor($x / 8.0)
                    $bitIdx = 7 - ($x % 8)
                    $andMaskRow[$byteIdx] = [byte]($andMaskRow[$byteIdx] -bor (1 -shl $bitIdx))
                }
            }
            $bw.Write($andMaskRow, 0, $maskRowStride)
        }

        $bw.Flush()
        $frameBytes = $ms.ToArray()
        $bw.Close()
        $ms.Dispose()

        $frames.Add($frameBytes)
        $frameSizes.Add($size)
    }
    $src.Dispose()

    # Create ICO file
    $targetDir = Split-Path -Parent $TargetIcoPath
    if (-not (Test-Path -LiteralPath $targetDir)) {
        [IO.Directory]::CreateDirectory($targetDir) | Out-Null
    }

    $fs = [System.IO.File]::Create($TargetIcoPath)
    $outBw = New-Object System.IO.BinaryWriter $fs
    $outBw.Write([uint16]0) # Reserved
    $outBw.Write([uint16]1) # Type = 1 (Icon)
    $outBw.Write([uint16]$frames.Count)

    $offset = 6 + ($frames.Count * 16)
    for ($i = 0; $i -lt $frames.Count; $i++) {
        $s = $frameSizes[$i]
        $wByte = if ($s -ge 256) { [byte]0 } else { [byte]$s }
        $hByte = if ($s -ge 256) { [byte]0 } else { [byte]$s }
        $outBw.Write($wByte)
        $outBw.Write($hByte)
        $outBw.Write([byte]0) # Colors
        $outBw.Write([byte]0) # Reserved
        $outBw.Write([uint16]1) # Planes
        $outBw.Write([uint16]32) # BitCount
        $outBw.Write([uint32]$frames[$i].Length) # Size
        $outBw.Write([uint32]$offset) # Offset
        $offset += $frames[$i].Length
    }

    for ($i = 0; $i -lt $frames.Count; $i++) {
        $outBw.Write($frames[$i])
    }

    $outBw.Close()
    $fs.Close()
    Write-Host "Generated Win32 DIB ICO: $TargetIcoPath ($([System.IO.FileInfo]::new($TargetIcoPath).Length) bytes, $($frames.Count) frames)"
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$iconsDir = Join-Path $repoRoot 'src\settings-ui\Settings.UI\Assets\Settings\Icons'

Convert-PngToWin32DibIco (Join-Path $iconsDir 'Localserver.png') (Join-Path $repoRoot 'src\modules\Localserver\LocalserverWorker\Localserver.ico')
Convert-PngToWin32DibIco (Join-Path $iconsDir 'UDPtest.png') (Join-Path $repoRoot 'src\modules\UDPtest\UDPtestService\UDPtest.ico')
Convert-PngToWin32DibIco (Join-Path $iconsDir 'NetMap.png') (Join-Path $repoRoot 'src\modules\NetMap\NetMapService\NetMap.ico')
Convert-PngToWin32DibIco (Join-Path $iconsDir 'AiHub.png') (Join-Path $repoRoot 'src\modules\AIHub\AIHubWorker\AIHub.ico')
