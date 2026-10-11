# Original geometric artwork; orange sampled from the installed ProPresenter icon.
# Keep the geometry synchronized with app-icon.svg. No vendor artwork is included.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function RoundedPath([float]$x,[float]$y,[float]$w,[float]$h,[float]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = 2*$r
    $path.AddArc($x,$y,$d,$d,180,90); $path.AddArc($x+$w-$d,$y,$d,$d,270,90)
    $path.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90); $path.AddArc($x,$y+$h-$d,$d,$d,90,90)
    $path.CloseFigure(); return ,$path
}
$master = [System.Drawing.Bitmap]::new(1024,1024)
$g = [System.Drawing.Graphics]::FromImage($master)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.ScaleTransform(4,4)
$orange = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#FF6C1A'))
$white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$tile = RoundedPath 8 8 240 240 52; $g.FillPath($orange,$tile)
$card = RoundedPath 88 58 120 140 16
$outline = [System.Drawing.Pen]::new([System.Drawing.Color]::White,12); $g.DrawPath($outline,$card)
$g.FillEllipse($white,163,82,18,18)
$mountains = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(108,129),[System.Drawing.PointF]::new(129,103),[System.Drawing.PointF]::new(149,124),[System.Drawing.PointF]::new(159,115),[System.Drawing.PointF]::new(184,140),[System.Drawing.PointF]::new(108,140))
$g.FillPolygon($white,$mountains)
$arrow = [System.Drawing.Drawing2D.GraphicsPath]::new()
$arrow.AddLine(40,166,144,166); $arrow.StartFigure(); $arrow.AddLines([System.Drawing.PointF[]]@([System.Drawing.PointF]::new(120,142),[System.Drawing.PointF]::new(144,166),[System.Drawing.PointF]::new(120,190)))
foreach ($width in @(30,16)) {
    $pen = [System.Drawing.Pen]::new($(if($width -eq 30){$orange.Color}else{$white.Color}),$width)
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen,$arrow); $pen.Dispose()
}
$sizes = @(16,20,24,32,40,48,64,128,256)
$frames = @{}
foreach ($size in ($sizes + @(150,512))) {
    $bmp = [System.Drawing.Bitmap]::new($size,$size)
    $render = [System.Drawing.Graphics]::FromImage($bmp)
    $render.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $render.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $render.DrawImage($master,0,0,$size,$size)
    $stream = [System.IO.MemoryStream]::new(); $bmp.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames[$size] = $stream.ToArray()
    if($size -in @(150,256,512)) { [System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "app-icon-$size.png"),$frames[$size]) }
    $stream.Dispose(); $render.Dispose(); $bmp.Dispose()
}
$ico = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app-icon.ico'))
$writer = [System.IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
foreach($size in $sizes) {
    $dimension = if($size -eq 256){0}else{$size}
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$size].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$size].Length
}
foreach($size in $sizes) { $writer.Write([byte[]]$frames[$size]) }
$writer.Dispose(); $g.Dispose(); $master.Dispose(); $orange.Dispose(); $white.Dispose(); $tile.Dispose(); $card.Dispose(); $outline.Dispose(); $arrow.Dispose()
