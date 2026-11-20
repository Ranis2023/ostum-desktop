param(
    [string]$Source=(Join-Path $env:LOCALAPPDATA 'SferaAI\acceleration\llama-b12547-cuda12'),
    [string]$Destination
)
$ErrorActionPreference='Stop'
if(!(Test-Path -LiteralPath $Source)){return}
$root=Split-Path -Parent $PSScriptRoot
$catalog=Get-Content -LiteralPath (Join-Path $root 'src\catalog.json') -Raw -Encoding utf8|ConvertFrom-Json
$dest=if($Destination){[IO.Path]::GetFullPath($Destination)}else{Join-Path $root 'dist\Acceleration'}
[void][IO.Directory]::CreateDirectory($dest)
foreach($file in $catalog.acceleration.libraries){
    $src=Join-Path $Source $file.name
    if((Get-Item -LiteralPath $src).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $src).Hash -ne $file.sha256){throw ('CUDA library verification failed: '+$file.name)}
    $target=Join-Path $dest $file.name
    if(Test-Path -LiteralPath $target){
        if((Get-FileHash -LiteralPath $target).Hash -ne $file.sha256){throw ('Different library exists: '+$file.name)}
        continue
    }
    try{New-Item -ItemType HardLink -Path $target -Target $src -ErrorAction Stop|Out-Null}
    catch{
        $temp=$target+'.'+[Guid]::NewGuid().ToString('N')+'.part'
        try{Copy-Item -LiteralPath $src -Destination $temp; if((Get-FileHash -LiteralPath $temp).Hash -ne $file.sha256){throw 'CUDA copy checksum mismatch'}; [IO.File]::Move($temp,$target)}
        finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp}}
    }
}
