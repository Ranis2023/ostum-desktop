param([switch]$Live,[switch]$NotifyWindows,[string]$Out)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$fx=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$app=Join-Path $root '.build'
if(!$Out){$Out=Join-Path $app ('update-test/'+[Guid]::NewGuid().ToString('N'))}
$test=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
if($Live){
    # Build an explicitly older test client in a separate tree. Production versions and profiles stay intact.
    $tree=Join-Path $test 'client'
    foreach($folder in @('assets','packaging','subscription','src','.build')){[void][IO.Directory]::CreateDirectory((Join-Path $tree $folder))}
    Copy-Item -LiteralPath (Join-Path $root 'build.ps1') -Destination $tree
    Copy-Item -Path (Join-Path $root 'src/*') -Destination (Join-Path $tree 'src') -Recurse -Force
    foreach($file in @('assets/ostum.png','packaging/Icon.ps1','packaging/Libraries.ps1','packaging/app.manifest','subscription/issuer-public.xml')){
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $tree $file) -Force
    }
    $source=Join-Path $tree 'src/Desktop.cs';$contents=[IO.File]::ReadAllText($source)
    $contents=[regex]::Replace($contents,'(?<=AssemblyVersion\(")[0-9.]+(?="\))','0.0.1.0')
    [IO.File]::WriteAllText($source,$contents,[Text.UTF8Encoding]::new($true))
    $policy=Join-Path $tree 'src/WorkPolicy.cs';$contents=[IO.File]::ReadAllText($policy)
    $anchor='Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)'
    if(!$contents.Contains($anchor)){throw 'Cannot isolate the test policy'}
    [IO.File]::WriteAllText($policy,$contents.Replace($anchor,'Store.Home'),[Text.UTF8Encoding]::new($true))
    $app=Join-Path $tree '.build'
    Get-ChildItem -LiteralPath (Join-Path $root '.build') -Filter '*.nupkg' -File|Copy-Item -Destination $app -Force
    & (Join-Path $tree 'build.ps1') -AppOnly
}
$exe=Join-Path $app 'UpdateChecks.exe'
$refs='System.dll','System.Core.dll','System.Xaml.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','WPF/WindowsBase.dll'|ForEach-Object {'/r:'+(Join-Path $fx $_)}
& (Join-Path $fx 'csc.exe') /nologo /optimize+ /codepage:65001 /target:exe /platform:x64 @refs ('/r:'+(Join-Path $app 'OstumDesktop.exe')) ('/out:'+$exe) (Join-Path $PSScriptRoot 'UpdateChecks.cs')
if($LASTEXITCODE){throw 'Could not compile updater checks'}
Copy-Item -LiteralPath (Join-Path $app 'OstumDesktop.exe.config') -Destination ($exe+'.config') -Force
& $exe
if($LASTEXITCODE){throw 'Release metadata checks failed'}
if($Live){
    $latest=Invoke-RestMethod -Headers @{'User-Agent'='OSTUM-update-test'} -Uri 'https://api.github.com/repos/Ranis2023/ostum-desktop/releases/latest'
    $modes=@('managed','off','notify','auto');if($NotifyWindows){$modes+='windows'}
    foreach($mode in $modes){
        $profile=Join-Path $test ($mode+'-'+[Guid]::NewGuid().ToString('N'))
        & $exe $profile $latest.tag_name.Substring(1) $mode
        if($LASTEXITCODE){throw "Update integration failed in $mode mode"}
    }
}
