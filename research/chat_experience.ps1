param([Parameter(Mandatory)][string]$Out,[string]$Build,[switch]$Deep)
$ErrorActionPreference='Stop'
$installed=Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop'
if(!$Build){$Build=$installed}
$target=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
& (Join-Path $PSScriptRoot 'app_probe.ps1') -Out $target -Build $Build -PrepareOnly
$dir=Join-Path $target 'runner'
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$refs='System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','System.Web.Extensions.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll'|ForEach-Object {'/r:'+(Join-Path $framework $_)}
$exe=Join-Path $dir 'ChatExperienceProbe.exe'
& (Join-Path $framework 'csc.exe') /nologo /optimize+ /platform:x64 /target:winexe /codepage:65001 @refs ('/r:'+(Join-Path $dir 'OstumDesktop.exe')) ('/out:'+$exe) (Join-Path $PSScriptRoot 'ChatExperienceProbe.cs')
if($LASTEXITCODE){throw 'UI probe did not compile.'}
Copy-Item -LiteralPath (Join-Path $dir 'OstumDesktop.exe.config') -Destination ($exe+'.config') -Force
$argv=@($target,(Join-Path $env:LOCALAPPDATA 'SferaAI/library/models'),(Join-Path $installed 'engines'),$(if($Deep){'deep'}else{'quick'}))|ForEach-Object {'"'+$_+'"'}
$p=Start-Process -FilePath $exe -ArgumentList $argv -WindowStyle Hidden -PassThru
if(!$p.WaitForExit(180000)){$p.Kill();throw 'UI measurement timed out.'}
if($p.ExitCode){Get-Content -Encoding UTF8 -LiteralPath (Join-Path $target 'error.txt');throw 'UI probe stopped.'}
Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $target 'results.json')
