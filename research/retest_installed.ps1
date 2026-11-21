param([Parameter(Mandatory)][string]$Out,[Parameter(Mandatory)][string]$Fixtures)
$ErrorActionPreference='Stop'
$src=Split-Path $PSScriptRoot -Parent
$dest=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
$fixturesDir=(Get-Item -LiteralPath $Fixtures).FullName
if(Get-ChildItem -LiteralPath $dest -Force){throw 'Choose a fresh directory for the isolated conversations and workbooks.'}
$installed=Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop'
foreach($name in 'OstumDesktop.exe','OstumTools.zip','OstumTools.zip.sha256','OSTUM-Help.html'){
    $product=Join-Path $installed $name
    $build=Join-Path (Join-Path $src '.build') $name
    if((Get-FileHash -LiteralPath $product).Hash -ne (Get-FileHash -LiteralPath $build).Hash){throw "Installed file differs from the build: $name"}
}
function Phase($name,[scriptblock]$run){
    Add-Content -LiteralPath (Join-Path $dest 'progress.log') -Value ("START "+$name+" "+(Get-Date -Format HH:mm:ss))
    & $run *> (Join-Path $dest ($name+'.log'))
    if($LASTEXITCODE){throw "$name failed with exit code $LASTEXITCODE"}
    Add-Content -LiteralPath (Join-Path $dest 'progress.log') -Value ("END "+$name+" "+(Get-Date -Format HH:mm:ss))
}
Phase 'chat' { & (Join-Path $PSScriptRoot 'app_probe.ps1') -Out (Join-Path $dest 'baseline') }
Phase 'business-chat' { & (Join-Path $PSScriptRoot 'app_probe.ps1') -Out (Join-Path $dest 'business') -Cases (Join-Path $PSScriptRoot 'workday_cases.json') }
Phase 'desk' { & (Join-Path $PSScriptRoot 'workday_flows.ps1') -Fixtures $fixturesDir -Out (Join-Path $dest 'flows') }
Phase 'access' { & (Join-Path $PSScriptRoot 'workspace_access_test.ps1') }
$line=(Get-Content -Encoding utf8 -LiteralPath (Join-Path $dest 'access.log')|Select-String 'Verified UI renders: ').Line
if(!$line){throw 'The isolated workspace acceptance fixture is missing.'}
$box=$line.Substring('Verified UI renders: '.Length)
Phase 'workspace' { & (Join-Path $PSScriptRoot 'workday_project.ps1') -FixtureRoot $box -Out (Join-Path $dest 'project') -Pairs (Join-Path $fixturesDir 'pairs.json') }
