[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$build=Join-Path $root '.build'
$test=Join-Path $build ('qa52/'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($test)
Add-Type -AssemblyName System.Security
$fx=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$csp=[Security.Cryptography.CspParameters]::new(24);$csp.Flags='CreateEphemeralKey'
$testKey=[Security.Cryptography.RSACryptoServiceProvider]::new(2048,$csp)
$public=Join-Path $test 'fixture-public.xml'
[IO.File]::WriteAllText($public,$testKey.ToXmlString($false))
$prod=Join-Path $root 'subscription/issuer-public.xml'
if([IO.File]::ReadAllText($prod).Trim() -eq $testKey.ToXmlString($false)){throw 'The fixture must have a distinct verification key.'}
foreach($file in @('Library.cs','WorkPolicy.cs')){
    $source=[IO.File]::ReadAllText((Join-Path $root ('src/'+$file)))
    $source=$source.Replace('Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)','AppDomain.CurrentDomain.BaseDirectory').Replace('Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)','AppDomain.CurrentDomain.BaseDirectory')
    [IO.File]::WriteAllText((Join-Path $test $file),$source)
}
$resources=@('/resource:'+$public+',issuer-public.xml')
foreach($file in @('WorkspaceView.xaml','WorkspaceTheme.xaml','ProAccessView.xaml','catalog.json','model-catalog.json','MainWindow.xaml','ModelsWindow.xaml','AgentLights.png','RoutePrompt.txt')){$resources+=('/resource:'+(Join-Path $root ('src/'+$file))+','+$file)}
$resources+=('/resource:'+(Join-Path $build 'icon.b64')+',icon.b64')
$refs=@('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Web.dll','System.Security.dll','System.Data.dll','System.Management.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll','System.Drawing.dll','netstandard.dll')|ForEach-Object {'/r:'+(Join-Path $fx $_)}
$refs+='/r:'+(Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET/assembly/GAC_MSIL/System.Speech') -Recurse -Filter System.Speech.dll|Select-Object -First 1).FullName
foreach($dll in @(& (Join-Path $root 'packaging/Libraries.ps1') -Build $build)){$refs+='/r:'+$dll;New-Item -ItemType HardLink -Path (Join-Path $test ([IO.Path]::GetFileName($dll))) -Target $dll|Out-Null}
foreach($n in @('OstumTools.zip','OstumTools.zip.sha256')){New-Item -ItemType HardLink -Path (Join-Path $test $n) -Target (Join-Path $build $n)|Out-Null}
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs'|Where-Object Name -notin @('Library.cs','WorkPolicy.cs')|ForEach-Object FullName)+@(Join-Path $test 'Library.cs';Join-Path $test 'WorkPolicy.cs';Join-Path $PSScriptRoot 'WorkspaceChecks.cs')
$exe=Join-Path $test 'WorkspaceChecks.exe'
& (Join-Path $fx 'csc.exe') /nologo /platform:x64 /codepage:65001 /target:exe /main:WorkspaceChecks ('/out:'+$exe) @resources @refs @files
if($LASTEXITCODE){throw 'Workspace acceptance checks failed to compile.'}
function Sign-Fixture([string]$Body){$sig=$testKey.SignData([Text.Encoding]::ASCII.GetBytes($Body),'SHA256');return $Body+'.'+[Convert]::ToBase64String($sig).TrimEnd('=').Replace('+','-').Replace('/','_')}
try{
    $device=(& $exe device).Trim();if($device -cnotmatch '^[a-zA-Z0-9_-]{24}$'){throw "Invalid fixture device."}
    $now=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();$body='OP1.'+$device+'.'+$now+'.'+($now+604800)
    $fixtures=@((Sign-Fixture $body),(Sign-Fixture ('OP1.'+$device+'.'+($now-604801)+'.'+($now-1))),(Sign-Fixture ('OP1.'+$device+'.'+$now+'.'+($now+604801))),(Sign-Fixture ('OP2.'+$device+'.'+$now+'.permanent')))
    $fixtures+=(Sign-Fixture ('OP2.'+$device+'.'+$now+'.temporary'))
    [IO.File]::WriteAllLines((Join-Path $test 'fixtures.txt'),[string[]]$fixtures)
    & $exe migrate; if($LASTEXITCODE){throw 'Offline legacy activation migration failed.'}
    & $exe verify; if($LASTEXITCODE){throw 'Workspace acceptance checks failed.'}
    & $exe restart; if($LASTEXITCODE){throw 'Offline restart failed.'}
    Write-Output ('Verified UI renders: '+$test)
} finally{$testKey.Dispose()}
