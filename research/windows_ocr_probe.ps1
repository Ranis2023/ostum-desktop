param([Parameter(Mandatory)][string]$Fixtures,[Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null=[Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null=[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
$taskMethod=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
    $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
} | Select-Object -First 1
function Await-WinRT($Operation,[type]$ResultType) {
    $task=$taskMethod.MakeGenericMethod($ResultType).Invoke($null,@($Operation))
    return $task.GetAwaiter().GetResult()
}
$language=[Windows.Globalization.Language]::new('ru')
$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($language)
if(!$engine){throw 'The Russian Windows OCR language is unavailable'}
$cases=@(@('scan-1.png','3174','12680'),@('scan-2.png','5408','20940'),@('scan-3.png','8261','17250'))
$sample=@(foreach($item in $cases){
    $stopwatch=[Diagnostics.Stopwatch]::StartNew()
    $file=Await-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync((Get-Item -LiteralPath (Join-Path $Fixtures $item[0])).FullName)) ([Windows.Storage.StorageFile])
    $stream=Await-WinRT ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    try{
        $decoder=Await-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap=Await-WinRT ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        try{
            $result=Await-WinRT ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
            $words=@($result.Lines|ForEach-Object {$_.Words}|Where-Object {$_.BoundingRect.Width -gt 0})
            [pscustomobject]@{file=$item[0];language=$engine.RecognizerLanguage.LanguageTag;correct=($result.Text.Contains($item[1]) -and $result.Text.Contains($item[2]));text=$result.Text;locatedWords=$words.Count;ms=$stopwatch.ElapsedMilliseconds}
        }finally{$bitmap.Dispose()}
    }finally{$stream.Dispose()}
})
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile),(ConvertTo-Json $sample),[Text.UTF8Encoding]::new($false))
$sample | Select-Object file,correct,locatedWords,ms
