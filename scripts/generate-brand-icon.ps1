param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

function New-TuringDeskPng([int]$Size) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = $Size / 256.0
        function S([double]$Value) { return [single]($Value * $scale) }

        $badgeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 18, 39, 78))
        $ringPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 49, 104, 212), (S 7))
        $innerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(190, 32, 72, 151), (S 4))
        $arrowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 58, 116, 255))
        $highlightBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 92, 148, 255))
        $glintBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(220, 120, 176, 255))

        try {
            $graphics.FillEllipse($badgeBrush, (S 30), (S 30), (S 196), (S 196))
            $graphics.DrawEllipse($ringPen, (S 30), (S 30), (S 196), (S 196))
            $graphics.DrawEllipse($innerPen, (S 47), (S 47), (S 162), (S 162))

            [System.Drawing.PointF[]]$arrow = @(
                [System.Drawing.PointF]::new((S 106), (S 73)),
                [System.Drawing.PointF]::new((S 168), (S 73)),
                [System.Drawing.PointF]::new((S 168), (S 56)),
                [System.Drawing.PointF]::new((S 208), (S 96)),
                [System.Drawing.PointF]::new((S 168), (S 136)),
                [System.Drawing.PointF]::new((S 168), (S 117)),
                [System.Drawing.PointF]::new((S 133), (S 117)),
                [System.Drawing.PointF]::new((S 150), (S 134)),
                [System.Drawing.PointF]::new((S 125), (S 159)),
                [System.Drawing.PointF]::new((S 76), (S 110)),
                [System.Drawing.PointF]::new((S 101), (S 85)),
                [System.Drawing.PointF]::new((S 125), (S 109)),
                [System.Drawing.PointF]::new((S 145), (S 89)),
                [System.Drawing.PointF]::new((S 106), (S 89))
            )
            $graphics.FillPolygon($arrowBrush, $arrow)

            [System.Drawing.PointF[]]$highlight = @(
                [System.Drawing.PointF]::new((S 168), (S 61)),
                [System.Drawing.PointF]::new((S 200), (S 96)),
                [System.Drawing.PointF]::new((S 168), (S 127)),
                [System.Drawing.PointF]::new((S 168), (S 114)),
                [System.Drawing.PointF]::new((S 151), (S 114)),
                [System.Drawing.PointF]::new((S 160), (S 104)),
                [System.Drawing.PointF]::new((S 176), (S 104)),
                [System.Drawing.PointF]::new((S 185), (S 96)),
                [System.Drawing.PointF]::new((S 176), (S 87)),
                [System.Drawing.PointF]::new((S 160), (S 87)),
                [System.Drawing.PointF]::new((S 151), (S 78)),
                [System.Drawing.PointF]::new((S 168), (S 78))
            )
            $graphics.FillPolygon($highlightBrush, $highlight)
            $graphics.FillEllipse($glintBrush, (S 114), (S 93), (S 12), (S 12))
        }
        finally {
            $badgeBrush.Dispose()
            $ringPen.Dispose()
            $innerPen.Dispose()
            $arrowBrush.Dispose()
            $highlightBrush.Dispose()
            $glintBrush.Dispose()
        }

        $pngStream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
            return $pngStream.ToArray()
        }
        finally {
            $pngStream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$images = foreach ($size in $sizes) {
    [PSCustomObject]@{
        Size = $size
        Bytes = New-TuringDeskPng $size
    }
}

$target = [System.IO.Path]::GetFullPath($OutputPath)
$parent = [System.IO.Path]::GetDirectoryName($target)
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
}

$stream = [System.IO.File]::Create($target)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    # ICONDIR: reserved, type=icon, image count.
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$images.Count)

    $offset = 6 + (16 * $images.Count)
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { [byte]0 } else { [byte]$image.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$image.Bytes.Length)
        $writer.Write([UInt32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write([byte[]]$image.Bytes)
    }
}
finally {
    $writer.Dispose()
    $stream.Dispose()
}

Write-Host "Generated TuringDesk multi-size icon: $target" -ForegroundColor Green
