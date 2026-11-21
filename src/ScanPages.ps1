$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -AssemblyName System.Drawing
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null=[Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null=[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
$null=[Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$methods=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1}
$generic=$methods | Where-Object {$_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'} | Select-Object -First 1
$action=$methods | Where-Object {!$_.IsGenericMethod -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'} | Select-Object -First 1
function Result($operation,[type]$type){$generic.MakeGenericMethod($type).Invoke($null,@($operation)).GetAwaiter().GetResult()}
try{
    $engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new('ru'))
    if(!$engine){$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()}
    if(!$engine){throw 'Установите компонент распознавания текста для нужного языка в настройках Windows.'}
    $file=Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync($env:OSTUM_SCAN_SOURCE)) ([Windows.Storage.StorageFile])
    $pdf=Result ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    $pictures=0
    $pages=@(foreach($n in ($env:OSTUM_SCAN_PAGES -split ',')){
        $page=$pdf.GetPage([uint32]([int]$n-1))
        $stream=[Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
        try{
            $options=[Windows.Data.Pdf.PdfPageRenderOptions]::new()
            $scale=[Math]::Min(2.5,[Math]::Min(2048,[Windows.Media.Ocr.OcrEngine]::MaxImageDimension)/[Math]::Max($page.Size.Width,$page.Size.Height))
            $options.DestinationWidth=[uint32][Math]::Max(1,$page.Size.Width*$scale)
            $options.DestinationHeight=[uint32][Math]::Max(1,$page.Size.Height*$scale)
            $null=$action.Invoke($null,@($page.RenderToStreamAsync($stream,$options))).GetAwaiter().GetResult()
            $stream.Seek(0)
            $decoder=Result ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap=Result ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,[Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)) ([Windows.Graphics.Imaging.SoftwareBitmap])
            try{
                $found=Result ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
                $lines=@(foreach($line in $found.Lines){
                    $boxes=@($line.Words | ForEach-Object {$_.BoundingRect})
                    if(!$boxes.Count){continue}
                    $left=($boxes.X|Measure-Object -Minimum).Minimum
                    $top=($boxes.Y|Measure-Object -Minimum).Minimum
                    $right=($boxes|ForEach-Object {$_.X+$_.Width}|Measure-Object -Maximum).Maximum
                    $bottom=($boxes|ForEach-Object {$_.Y+$_.Height}|Measure-Object -Maximum).Maximum
                    @{Text=$line.Text;Box=@($left/$bitmap.PixelWidth;$top/$bitmap.PixelHeight;($right-$left)/$bitmap.PixelWidth;($bottom-$top)/$bitmap.PixelHeight)}
                })
                $picture=$null
                if($lines.Count -and $pictures -lt 4MB -and $env:OSTUM_SCAN_PREVIEW -eq '1'){
                    $stream.Seek(0);$net=[System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream)
                    $im=[Drawing.Image]::FromStream($net);$ratio=[Math]::Min(1.0,760/[Math]::Max($im.Width,$im.Height))
                    $thumb=[Drawing.Bitmap]::new($im,[int][Math]::Max(1,$ratio*$im.Width),[int][Math]::Max(1,$ratio*$im.Height))
                    $memory=[IO.MemoryStream]::new()
                    try{$thumb.Save($memory,[Drawing.Imaging.ImageFormat]::Jpeg);$picture=[Convert]::ToBase64String($memory.ToArray());$pictures+=$picture.Length}
                    finally{$memory.Dispose();$thumb.Dispose();$im.Dispose();$net.Dispose()}
                }
                @{Number=[int]$n;Language=$engine.RecognizerLanguage.LanguageTag;Picture=$picture;Lines=$lines}
            }finally{$bitmap.Dispose()}
        }finally{$stream.Dispose();$page.Dispose()}
    })
    [Console]::Write((ConvertTo-Json -InputObject $pages -Depth 6 -Compress))
}catch{[Console]::Error.Write($_.Exception.GetBaseException().Message);exit 1}
