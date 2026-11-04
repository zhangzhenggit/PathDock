$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$sizes=@(16,24,32,48,64,128,256)
$frames=[Collections.Generic.List[byte[]]]::new()
foreach($size in $sizes){
  $visual=[Windows.Media.DrawingVisual]::new()
  $drawing=$visual.RenderOpen()
  $drawing.PushTransform([Windows.Media.ScaleTransform]::new($size/256.0,$size/256.0))
  $back=[Windows.Media.Geometry]::Parse('M32,67 Q32,48 51,48 L100,48 Q108,48 114,55 L130,72 L205,72 Q224,72 224,91 L224,192 Q224,212 204,212 L52,212 Q32,212 32,192 Z')
  $drawing.DrawGeometry([Windows.Media.BrushConverter]::new().ConvertFromString('#69B8F5'),$null,$back)
  $front=[Windows.Media.Geometry]::Parse('M32,94 Q32,82 45,82 L211,82 Q224,82 224,95 L224,192 Q224,212 204,212 L52,212 Q32,212 32,192 Z')
  $gradient=[Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#268DF0'),[Windows.Media.ColorConverter]::ConvertFromString('#1261C6'),90)
  $drawing.DrawGeometry($gradient,$null,$front)
  $pen=[Windows.Media.Pen]::new([Windows.Media.Brushes]::White,12)
  $pen.StartLineCap=[Windows.Media.PenLineCap]::Round;$pen.EndLineCap=[Windows.Media.PenLineCap]::Round;$pen.LineJoin=[Windows.Media.PenLineJoin]::Round
  $drawing.DrawGeometry($null,$pen,[Windows.Media.Geometry]::Parse('M78,165 L110,165 Q126,165 126,149 L126,139 Q126,123 142,123 L178,123'))
  $drawing.DrawEllipse([Windows.Media.Brushes]::White,$null,[Windows.Point]::new(78,165),11,11)
  $drawing.DrawEllipse([Windows.Media.BrushConverter]::new().ConvertFromString('#77F1DB'),$null,[Windows.Point]::new(178,123),12,12)
  $drawing.Pop();$drawing.Close()
  $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
  $bitmap.Render($visual)
  $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
  $memory=[IO.MemoryStream]::new();$encoder.Save($memory);$bytes=$memory.ToArray();$memory.Dispose();$frames.Add($bytes)
  if($size -eq 256){[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'PathDock.png'),$bytes)}
}
$stream=[IO.File]::Create((Join-Path $PSScriptRoot 'PathDock.ico'))
$writer=[IO.BinaryWriter]::new($stream)
try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  for($i=0;$i -lt $sizes.Count;$i++){
    $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
    $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
    $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
    $offset+=$frames[$i].Length
  }
  foreach($frame in $frames){$writer.Write($frame)}
}finally{$writer.Dispose();$stream.Dispose()}
