param([Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][string]$Out,[Parameter(Mandatory)][string]$Pairs,[switch]$PrepareOnly)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$box=(Get-Item -LiteralPath $FixtureRoot).FullName
$allowed=(Get-Item -LiteralPath (Join-Path $root '.build/qa52')).FullName.TrimEnd('\')+'\'
if(!$box.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or !(Test-Path (Join-Path $box 'fixture-public.xml'))){throw 'Run workspace_access_test.ps1 and use its isolated fixture directory'}
$outDir=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
$engine=Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop/engines'
$junction=Join-Path $box 'engines';if(!(Test-Path $junction)){New-Item -ItemType Junction -Path $junction -Target $engine|Out-Null}
$fx=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs='System.dll','System.Core.dll','System.Xaml.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Web.Extensions.dll'|ForEach-Object {'/r:'+(Join-Path $fx $_)}
$exe=Join-Path $box 'WorkdayProject.exe'
& (Join-Path $fx 'csc.exe') /nologo /optimize+ /codepage:65001 /platform:x64 /target:winexe @refs ('/r:'+(Join-Path $box 'WorkspaceChecks.exe')) ('/out:'+$exe) (Join-Path $PSScriptRoot 'WorkdayProject.cs')
if($LASTEXITCODE){throw 'Project test compilation failed'}
$config=Join-Path $root 'src/OstumDesktop.exe.config';Copy-Item -LiteralPath $config -Destination ($exe+'.config') -Force
if($PrepareOnly){return}
$argv=@($outDir,(Join-Path $env:LOCALAPPDATA 'SferaAI/library/models'),$engine,(Get-Item -LiteralPath $Pairs).FullName)|ForEach-Object {'"'+$_+'"'}
$proc=Start-Process -FilePath $exe -ArgumentList $argv -WindowStyle Hidden -PassThru
if(!$proc.WaitForExit(18*60*1000)){$proc.Kill();throw 'Project test exceeded the time limit'}
if($proc.ExitCode){Get-Content -LiteralPath (Join-Path $outDir 'error.log') -Encoding utf8;throw 'Project test aborted'}
$cases=@(Get-Content -LiteralPath (Join-Path $outDir 'project.json') -Encoding utf8 -Raw|ConvertFrom-Json)
Write-Output ("Project and training: {0}/{1}" -f @($cases|Where-Object passed).Count,$cases.Count)
