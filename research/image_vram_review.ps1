param([Parameter(Mandatory)][string]$Out,[string]$Models=(Join-Path $env:LOCALAPPDATA 'SferaAI/library/models'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dest=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
$catalog=(Get-Content -LiteralPath (Join-Path $root 'src/catalog.json') -Raw -Encoding utf8|ConvertFrom-Json).files
$weights=@{}
foreach($id in 'zimage','zte','zvae'){
    $file=$catalog|Where-Object id -eq $id
    $weights[$id]=(Get-Item -LiteralPath (Join-Path $Models $file.name)).FullName
}
$exe=(Get-Item -LiteralPath (Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop/engines/sd-532d2c1/sd-cli.exe')).FullName
Add-Type -AssemblyName System.Drawing
$promptFile=Join-Path $dest 'prompt.txt'
[IO.File]::WriteAllText($promptFile,'A white porcelain coffee cup on a plain blue table, soft daylight, no lettering.',[Text.UTF8Encoding]::new($false))
$runs=@(foreach($mode in 'default','budget'){
    $png=Join-Path $dest ($mode+'.png')
    $argsList=@('--diffusion-model',$weights.zimage,'--llm',$weights.zte,'--vae',$weights.zvae,'--mmap','--vae-tiling','--steps','8','--cfg-scale','1','--sampling-method','euler','--prompt-file',$promptFile,'-o',$png,'-W','1024','-H','1024','-t','8','--seed','19726')
    if($mode -eq 'budget'){$argsList+=@('--max-vram','3.25','--disable-prefetch')}
    $free=& nvidia-smi.exe --query-gpu=memory.free --format=csv,noheader,nounits
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $quoted=$argsList|ForEach-Object {'"'+$_+'"'}
    $p=Start-Process -FilePath $exe -ArgumentList $quoted -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dest ($mode+'-out.log')) -RedirectStandardError (Join-Path $dest ($mode+'-err.log'))
    $finished=$p.WaitForExit(240000)
    if(!$finished){$p.Kill();$p.WaitForExit()}
    $watch.Stop();$valid=$false
    if((Test-Path -LiteralPath $png) -and $p.ExitCode -eq 0){
        $im=[Drawing.Bitmap]::new($png)
        try{$valid=($im.Width -eq 1024 -and $im.Height -eq 1024)}finally{$im.Dispose()}
    }
    [pscustomobject]@{mode=$mode; freeMiB=[int]($free|Select-Object -First 1); completed=$valid; exit=$p.ExitCode; timeout=!$finished; seconds=[Math]::Round($watch.Elapsed.TotalSeconds,1)}
})
$lines=@($runs|ForEach-Object { $label=if($_.mode -eq 'default'){'Обычный режим'}else{'Бюджет 3,25 ГиБ без предзагрузки'}; $outcome=if($_.completed){'получен PNG 1024 × 1024'}elseif($_.timeout){'остановлен через 4 минуты'}else{'генерация завершилась без PNG'}; "$label — $outcome за $($_.seconds) с, свободно перед запуском $($_.freeMiB) МиБ" })
[pscustomobject]@{runs=$runs;description=($lines -join '. ')+'.'}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $dest 'experiment.json') -Encoding utf8
