param([Parameter(Mandatory)][string]$Build)
$ErrorActionPreference='Stop'
$dir=(Resolve-Path -LiteralPath $Build).Path
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe=Join-Path $dir 'GuardTests.exe'
& $compiler /nologo /optimize+ /codepage:65001 /target:exe /platform:x64 /r:System.Web.Extensions.dll ('/r:'+(Join-Path $dir 'OstumDesktop.exe')) ('/out:'+$exe) (Join-Path $PSScriptRoot 'GuardTests.cs')
if($LASTEXITCODE){throw 'Guard test compilation failed'}
& $exe
if($LASTEXITCODE){throw 'Guard test failed'}
