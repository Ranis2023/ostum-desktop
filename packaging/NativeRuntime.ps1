param([Parameter(Mandatory=$true)][string]$Build)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$redist=Join-Path $root 'dist\Prerequisites\VC_redist.x64.exe'
if(!(Test-Path -LiteralPath $redist)){
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $redist))
    & curl.exe -fsSL --retry 3 -o ($redist+'.part') 'https://aka.ms/vc14/vc_redist.x64.exe'
    if($LASTEXITCODE -ne 0){throw 'Microsoft redistributable download failed'}
    [IO.File]::Move($redist+'.part',$redist)
}
$signature=Get-AuthenticodeSignature -LiteralPath $redist
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw 'Microsoft redistributable signature check failed'}
$unpacked=Join-Path $Build 'vc-package'
& (Join-Path $Build 'wix\dark.exe') -nologo -x $unpacked $redist | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not extract Microsoft redistributable'}
$cab=Join-Path $unpacked 'AttachedContainer\packages\vcRuntimeMinimum_amd64\cab1.cab'
$target=Join-Path $Build 'msvc'
[void][IO.Directory]::CreateDirectory($target)
foreach($dll in @('msvcp140.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','vcomp140.dll')){
    $id=$dll+'_amd64'
    & (Join-Path $env:WINDIR 'System32\expand.exe') ('-F:'+$id) $cab $target | Out-Null
    if($LASTEXITCODE -ne 0){throw ('Could not extract '+$dll)}
    $dst=Join-Path $target $dll
    [IO.File]::Copy((Join-Path $target $id),$dst,$true)
    $signature=Get-AuthenticodeSignature -LiteralPath $dst
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw ('Invalid native runtime: '+$dll)}
}
