# Renders assets/icon.ico (16-256 px, each size drawn natively) and assets/icon.png. Run once; outputs are committed.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function Draw-Icon([int]$s) {
    $bmp = [Drawing.Bitmap]::new($s, $s)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    # Rounded square with an indigo -> violet gradient
    $m = [Math]::Max(1, $s * 0.04); $w = $s - 2 * $m; $r = $w * 0.24
    $p = [Drawing.Drawing2D.GraphicsPath]::new()
    $p.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    $bg = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.PointF]::new(0, 0), [Drawing.PointF]::new($s, $s),
        [Drawing.Color]::FromArgb(255, 79, 70, 229), [Drawing.Color]::FromArgb(255, 192, 38, 211))
    $g.FillPath($bg, $p)

    # White play triangle, shifted right to leave room for speed lines
    $white = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $cx = $s * ($(if ($s -ge 32) { 0.58 } else { 0.54 })); $cy = $s / 2; $h = $s * 0.42
    $tri = [Drawing.PointF[]]@(
        [Drawing.PointF]::new($cx - $h * 0.42, $cy - $h / 2),
        [Drawing.PointF]::new($cx + $h * 0.5, $cy),
        [Drawing.PointF]::new($cx - $h * 0.42, $cy + $h / 2))
    $g.FillPolygon($white, $tri)

    # Speed lines ("send") — dropped at small sizes where they'd turn to mush
    if ($s -ge 32) {
        $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(220, 255, 255, 255), [float]($s * 0.055))
        $pen.StartCap = $pen.EndCap = 'Round'
        $x1 = $s * 0.20; $x2 = $cx - $h * 0.55
        foreach ($k in @(@(-0.16, 0.10), @(0, 0), @(0.16, 0.10))) {
            $y = $cy + $s * $k[0]
            $g.DrawLine($pen, [float]($x1 + $s * $k[1]), [float]$y, [float]$x2, [float]$y)
        }
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $b = Draw-Icon $s; $ms = [IO.MemoryStream]::new(); $b.Save($ms, 'Png'); $b.Dispose(); , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$out = [IO.BinaryWriter]::new([IO.File]::Create("$PSScriptRoot\icon.ico"))
$out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = $sizes[$i] % 256  # 0 means 256
    $out.Write([byte]$d); $out.Write([byte]$d); $out.Write([byte]0); $out.Write([byte]0)
    $out.Write([uint16]1); $out.Write([uint16]32); $out.Write([uint32]$pngs[$i].Length); $out.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $out.Write($png) }
$out.Dispose()
[IO.File]::WriteAllBytes("$PSScriptRoot\icon.png", $pngs[-1])
"icon.ico + icon.png written"
