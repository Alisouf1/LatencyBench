Add-Type -AssemblyName System.Drawing

$OutPath = $args[0]

function ConvertTo-IconDib {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height

    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bitmap.UnlockBits($data)

    # An icon DIB stores rows bottom-up, and declares a height of 2x so the colour plane and the AND
    # mask can share one BITMAPINFOHEADER.
    $andStride = [int][Math]::Floor(($w + 31) / 32) * 4
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($out)

    $bw.Write([UInt32]40)          # biSize
    $bw.Write([Int32]$w)           # biWidth
    $bw.Write([Int32]($h * 2))     # biHeight: colour rows + mask rows
    $bw.Write([UInt16]1)           # biPlanes
    $bw.Write([UInt16]32)          # biBitCount
    $bw.Write([UInt32]0)           # biCompression: BI_RGB
    $bw.Write([UInt32](($w * $h * 4) + ($andStride * $h)))
    $bw.Write([Int32]0); $bw.Write([Int32]0)   # pels per metre
    $bw.Write([UInt32]0); $bw.Write([UInt32]0) # palette

    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, $y * $stride, $w * 4)
    }

    # The AND mask is left all-zero: with a 32bpp entry Windows composites from the alpha channel,
    # and a zero mask means "no pixel forced transparent by the mask".
    $blankMaskRow = New-Object byte[] $andStride
    for ($y = 0; $y -lt $h; $y++) {
        $bw.Write($blankMaskRow, 0, $andStride)
    }

    $bw.Flush()
    $bytes = $out.ToArray()
    $bw.Dispose(); $out.Dispose()
    return , $bytes
}

# Rendered at each size independently rather than downscaled from one bitmap: a spike thin enough to
# look right at 256px disappears entirely at 16px, so the stroke weight is a function of the size.
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()

foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size
    # Inset keeps the rounded square off the very edge so it reads as an object, not a filled tile.
    $pad = [Math]::Max(1.0, $s * 0.055)
    $box = $s - (2 * $pad)
    $radius = $s * 0.22

    # Rounded-rectangle body with a vertical gradient.
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $box - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $box - $d, $pad + $box - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $box - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF($pad, $pad, $box, $box)
    $top = [System.Drawing.Color]::FromArgb(255, 24, 33, 51)
    $bottom = [System.Drawing.Color]::FromArgb(255, 9, 13, 22)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $top, $bottom, 90.0)
    $g.FillPath($grad, $path)

    # Hairline edge so the icon keeps its shape on a dark taskbar.
    $edgeWidth = [Math]::Max(0.7, $s * 0.012)
    $edgePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 120, 160, 210), $edgeWidth)
    $g.DrawPath($edgePen, $path)

    # The trace: a flat baseline interrupted by one hard latency spike. This is the whole idea of the
    # app in one glyph, and it survives being scaled down to 16px because the spike is the only
    # feature that has to be legible.
    $cx = $s * 0.5
    $baseline = $s * 0.62
    $peak = $s * 0.30
    $stroke = [Math]::Max(1.6, $s * 0.085)

    $pts = @(
        (New-Object System.Drawing.PointF(($s * 0.14), $baseline)),
        (New-Object System.Drawing.PointF(($cx - $s * 0.18), $baseline)),
        (New-Object System.Drawing.PointF(($cx - $s * 0.055), $peak)),
        (New-Object System.Drawing.PointF(($cx + $s * 0.055), ($baseline + $s * 0.10))),
        (New-Object System.Drawing.PointF(($cx + $s * 0.17), $baseline)),
        (New-Object System.Drawing.PointF(($s * 0.86), $baseline))
    )

    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 45, 212, 191), $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    # A soft glow under the peak, only where there are enough pixels for it to read as glow rather
    # than as a smudge. Drawn before the trace so it sits behind it — painting it afterwards would
    # wash out the sharp edge the spike depends on.
    if ($size -ge 48) {
        # Computed first: an arithmetic expression written inline in a New-Object argument list gets
        # parsed as extra array elements, not as one evaluated argument.
        $glowWidth = [single]($stroke * 2.4)
        $glowColor = [System.Drawing.Color]::FromArgb(70, 45, 212, 191)
        $glowPen = New-Object System.Drawing.Pen($glowColor, $glowWidth)
        $glowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $glowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $glowPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.DrawLines($glowPen, $pts[1..4])
        $glowPen.Dispose()
    }

    $g.DrawLines($pen, $pts)

    $g.Dispose()

    # Only the 256px entry is stored PNG-compressed. Every smaller size is written as a classic
    # 32bpp DIB: PNG entries below 256 are legal since Vista but are NOT universally decoded —
    # GDI+ (System.Drawing.Icon.ToBitmap) and several installer toolchains, Inno Setup among them,
    # reject them, which is exactly how an app ends up showing the generic icon in one place and the
    # real one in another.
    if ($size -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $ms.Dispose()
    }
    else {
        $pngs += , (ConvertTo-IconDib $bmp)
    }

    $bmp.Dispose()
    $path.Dispose(); $grad.Dispose(); $edgePen.Dispose(); $pen.Dispose()
}

# Pack as an ICO. Every entry is stored PNG-compressed, which Windows has supported since Vista and
# which keeps the 256px entry from dominating the file size.
$fs = [System.IO.File]::Create($OutPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)               # reserved
$bw.Write([UInt16]1)               # type: icon
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    # 256 is encoded as 0 in the directory — the field is a single byte.
    $bw.Write([Byte]$(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write([Byte]$(if ($sz -ge 256) { 0 } else { $sz }))
    $bw.Write([Byte]0)             # palette count
    $bw.Write([Byte]0)             # reserved
    $bw.Write([UInt16]1)           # colour planes
    $bw.Write([UInt16]32)          # bits per pixel
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}

foreach ($png in $pngs) { $bw.Write($png) }

$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output "Wrote $OutPath ($((Get-Item $OutPath).Length) bytes, $($sizes.Count) sizes)"
