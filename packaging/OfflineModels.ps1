param(
    [string]$ModelDirectory = (Join-Path $env:LOCALAPPDATA 'SferaAI\library\models'),
    [string[]]$FileIds,
    [string]$OutputFolder,
    [switch]$Dictation,
    [switch]$Drawing,
    [switch]$Music,
    [switch]$Cuda
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$kit=if($OutputFolder){[IO.Path]::GetFullPath($OutputFolder)}else{Join-Path $root 'dist'}
$dest=Join-Path $kit 'Models'
[void][IO.Directory]::CreateDirectory($dest)
$catalog=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src\catalog.json') | ConvertFrom-Json
$files=$catalog.files
if(!$FileIds){$FileIds=@('q9','v9')}
if($Dictation){$FileIds+=@('voice')}
if($Drawing){$FileIds+=@('zimage','zte','zvae')}
$unknown=@($FileIds|Where-Object {$_ -notin $files.id})
if($unknown){throw ('Unknown model identifiers: '+($unknown -join ', '))}
$files=$files | Where-Object {$_.id -in $FileIds}
if($Music){$files=@($files)+@($catalog.music)}
foreach ($file in $files) {
    $srcDir=$ModelDirectory
    $destDir=$dest
    if($file.name -in $catalog.music.name){
        $srcDir=Join-Path $ModelDirectory 'music-ace15'
        $destDir=Join-Path $dest 'music-ace15'
        [void][IO.Directory]::CreateDirectory($destDir)
    }
    $src=Join-Path $srcDir $file.name
    if(!(Test-Path -LiteralPath $src) -and $file.legacyName){$src=Join-Path $ModelDirectory $file.legacyName}
    if(!(Test-Path -LiteralPath $src)){throw ('Missing selected model: '+$file.name)}
    if((Get-Item -LiteralPath $src).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -ne $file.sha256) {throw ('Model verification failed: '+$file.name)}
    $target=Join-Path $destDir $file.name
    if(Test-Path -LiteralPath $target) {
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.sha256){throw ('A different file exists at '+$target)}
        continue
    }
    try {
        New-Item -ItemType HardLink -Path $target -Target $src -ErrorAction Stop | Out-Null
    } catch {
        $partial=$target+'.'+[Guid]::NewGuid().ToString('N')+'.copying'
        try{
            Copy-Item -LiteralPath $src -Destination $partial -ErrorAction Stop
            if((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $file.sha256){throw ('Copy verification failed: '+$file.name)}
            [IO.File]::Move($partial,$target)
        }finally{if(Test-Path -LiteralPath $partial){Remove-Item -LiteralPath $partial}}
    }
    Write-Output $file.name
}
if($Cuda){& (Join-Path $PSScriptRoot 'OfflineAcceleration.ps1') -Destination (Join-Path $kit 'Acceleration')}
$setup=(Get-Item -LiteralPath (Join-Path $root 'dist/Ostum-Desktop-Setup.exe')).FullName
$kitSetup=Join-Path $kit 'Ostum-Desktop-Setup.exe'
if($setup -ne $kitSetup){Copy-Item -LiteralPath $setup -Destination $kitSetup -Force}
