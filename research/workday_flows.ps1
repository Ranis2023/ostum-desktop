param([Parameter(Mandatory)][string]$Out,[Parameter(Mandatory)][string]$Fixtures,
    [ValidateSet('office','media','repeat')][string[]]$Suite=@('office','media','repeat'),
    [string]$Models=(Join-Path $env:LOCALAPPDATA 'SferaAI/library/models'),[string]$Build)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$outDir=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
$files=(Get-Item -LiteralPath $Fixtures).FullName
$installed=Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop'
& (Join-Path $PSScriptRoot 'app_probe.ps1') -Out $outDir -Build $Build -PrepareOnly
$runner=Join-Path $outDir 'runner'
$fx=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs='System.dll','System.Core.dll','System.Drawing.dll','System.Web.Extensions.dll','System.Xaml.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll'|ForEach-Object {'/r:'+(Join-Path $fx $_)}
$exe=Join-Path $runner 'WorkdayFlows.exe'
& (Join-Path $fx 'csc.exe') /nologo /optimize+ /codepage:65001 /platform:x64 /target:winexe @refs ('/r:'+(Join-Path $runner 'OstumDesktop.exe')) ('/out:'+$exe) (Join-Path $PSScriptRoot 'WorkdayFlows.cs')
if($LASTEXITCODE){throw 'Workday probe compilation failed'}
Copy-Item -LiteralPath (Join-Path $runner 'OstumDesktop.exe.config') -Destination ($exe+'.config') -Force
foreach($suiteName in $Suite){
    $casePath=Join-Path $outDir $suiteName
    $argv=@($casePath,$files,$Models,(Join-Path $installed 'engines'),$suiteName)|ForEach-Object {'"'+$_+'"'}
    $proc=Start-Process -FilePath $exe -ArgumentList $argv -WindowStyle Hidden -PassThru
    if(!$proc.WaitForExit(22*60*1000)){$proc.Kill();throw ('Suite timeout: '+$suiteName)}
    if($proc.ExitCode){Get-Content -LiteralPath (Join-Path $casePath 'error.log') -Encoding utf8;throw ('Suite aborted: '+$suiteName)}
    $cases=@(Get-Content -LiteralPath (Join-Path $casePath 'flows.json') -Encoding utf8 -Raw|ConvertFrom-Json)
    Write-Output ("{0}: {1}/{2} met their acceptance criteria" -f $suiteName,@($cases|Where-Object passed).Count,$cases.Count)
}
