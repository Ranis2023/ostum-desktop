param(
    [ValidateSet('App','Network','Weights','Mathematics','Private')][string]$Study='App',
    [string]$Out,
    [string]$BeforeBuild,
    [string]$Python
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Out){$Out=Join-Path $root ('.build/audit-'+$Study+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$Out=[IO.Path]::GetFullPath($Out)
if(Test-Path -LiteralPath $Out){throw 'Choose a fresh output directory. This keeps earlier results available for comparison.'}
[void][IO.Directory]::CreateDirectory($Out)
if(!$Python){
    $bundled=Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
    $Python=if(Test-Path -LiteralPath $bundled){$bundled}else{(Get-Command python).Source}
}
$env:PYTHONIOENCODING='utf-8';$env:OPENBLAS_NUM_THREADS='2'
function RunStudy([string]$Script,[array]$Arguments){
    & $Python (Join-Path $PSScriptRoot $Script) @Arguments
    if($LASTEXITCODE){throw ($Script+' failed')}
}
$sources=Join-Path $Out 'sources'
if($Study -in 'Weights','Mathematics','App'){
    $frozen=Join-Path $root '.build/research321/sources'
    if(Test-Path -LiteralPath $frozen){
        [void][IO.Directory]::CreateDirectory($sources)
        Get-ChildItem -LiteralPath $frozen -File|Copy-Item -Destination $sources
    }
    RunStudy 'get_sources.py' @('--out',$sources)
}
switch($Study){
    'App'{
        $registry=Join-Path $Out registry
        RunStudy 'registry_study.py' @('--data',$sources,'--out',$registry)
        if(!$BeforeBuild){
            $previous=Join-Path $root '.build/research321/app-before/runner'
            if(Test-Path -LiteralPath $previous){$BeforeBuild=$previous}
        }
        if($BeforeBuild){
            & (Join-Path $PSScriptRoot 'app_probe.ps1') -Out (Join-Path $Out 'app-before') -Build $BeforeBuild
            & (Join-Path $PSScriptRoot 'app_probe.ps1') -Out (Join-Path $Out 'registry-before') -Build $BeforeBuild -Cases (Join-Path $registry 'cases.json')
        }
        & (Join-Path $PSScriptRoot 'app_probe.ps1') -Out (Join-Path $Out 'app-final')
        & (Join-Path $PSScriptRoot 'app_probe.ps1') -Out (Join-Path $Out 'registry-final') -Cases (Join-Path $registry 'cases.json')
        & (Join-Path $PSScriptRoot 'guard_tests.ps1') -Build (Join-Path $Out 'app-final/runner')
    }
    'Network'{
        foreach($mode in 'raw','current','kv8','final'){
            RunStudy 'native_audit.py' @('--variant',$mode,'--out',(Join-Path $Out $mode))
        }
    }
    'Weights'{
        $evidence=Join-Path $Out 'evidence'
        foreach($stage in 'freeze','baseline','fit','final'){
            RunStudy 'evidence_study.py' @($stage,'--archive',(Join-Path $sources 'RCB.zip'),'--out',$evidence)
        }
        foreach($stage in 'freeze','baseline','fit','final'){
            RunStudy 'transfer.py' @($stage,'--data',$sources,'--rcb',$evidence,'--out',(Join-Path $Out 'transfer'))
        }
    }
    'Mathematics'{
        $math=Join-Path $Out 'math'
        foreach($stage in 'freeze','baseline','develop'){
            RunStudy 'math_study.py' @($stage,'--data',$sources,'--out',$math)
        }
    }
    'Private'{RunStudy 'private_check.py' @('--out',(Join-Path $Out 'private-check'))}
}
Write-Output $Out
