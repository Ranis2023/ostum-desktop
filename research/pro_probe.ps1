param([string]$ModelDirectory=(Join-Path $env:LOCALAPPDATA 'SferaAI/library/models'),[string]$ImportFile,[string]$EngineDirectory=(Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop/engines'))
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fx=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$build=Join-Path $root '.build'
$exe=Join-Path $build 'ProProbe.exe'
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationFramework.dll','WPF\PresentationCore.dll')|ForEach-Object {'/r:'+(Join-Path $fx $_)}
& (Join-Path $fx csc.exe) /nologo /codepage:65001 /platform:x64 '/target:exe' ('/out:'+$exe) ('/r:'+(Join-Path $build 'OstumDesktop.exe')) @refs (Join-Path $PSScriptRoot 'ProProbe.cs')
if($LASTEXITCODE){throw 'Probe compilation failed'}
$arguments=@($EngineDirectory,$ModelDirectory)
if($ImportFile){$arguments+=(Resolve-Path -LiteralPath $ImportFile).Path}
& $exe @arguments
if($LASTEXITCODE){throw 'Native component probe failed'}
