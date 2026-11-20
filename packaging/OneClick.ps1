param([string]$Output)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$build=Join-Path $root '.build'
$dist=Join-Path $root 'dist'
if(!$Output){$Output=Join-Path $dist 'Ostum-Desktop-Setup.exe'}
$msi=Get-Item -LiteralPath (Join-Path $dist 'OstumDesktop.msi')
$app=Get-Item -LiteralPath (Join-Path $build 'OstumDesktop.exe')
$meta='namespace SferaSetup { static class Payload { public const long MsiBytes='+$msi.Length+'L; public const string MsiHash="'+(Get-FileHash -LiteralPath $msi.FullName).Hash+'"; public const string AppVersion="'+$app.VersionInfo.FileVersion+'"; } }'
$generated=Join-Path $build 'OstumPayload.cs'
[IO.File]::WriteAllText($generated,$meta)
$fx=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs=@('System.dll','System.Core.dll','System.Net.Http.dll','System.IO.Compression.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF\WindowsBase.dll','WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll')|ForEach-Object {'/r:'+(Join-Path $fx $_)}
& (Join-Path $PSScriptRoot 'Icon.ps1') -OutputFolder $build -Installer
$staged=Join-Path $build 'Ostum-Desktop-Setup.exe'
$sources=@('src/Library.cs','src/LocalModels.cs','src/WorkPolicy.cs','src/InstallModels.cs','src/Acceleration.cs','packaging/OneClick.cs')|ForEach-Object {Join-Path $root $_}
& (Join-Path $fx 'csc.exe') /nologo /optimize+ /codepage:65001 /target:winexe /platform:x64 @refs ('/out:'+$staged) ('/win32icon:'+(Join-Path $build 'setup.ico')) ('/win32manifest:'+(Join-Path $PSScriptRoot 'app.manifest')) ('/resource:'+(Join-Path $root 'src/catalog.json')+',catalog.json') ('/resource:'+$msi.FullName+',OstumDesktop.msi') $generated @sources
if($LASTEXITCODE){throw 'Installer compilation failed'}
$check=Start-Process -FilePath $staged -ArgumentList '--verify' -PassThru -WindowStyle Hidden
$check.WaitForExit()
if($check.ExitCode){throw 'Installer payload verification failed'}
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Output)))
Copy-Item -LiteralPath $staged -Destination $Output -Force
Get-Item -LiteralPath $Output|Select-Object FullName,Length
