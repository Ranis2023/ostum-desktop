param([switch]$UseExistingBuild,[string]$DistributionDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dist=if($DistributionDirectory){[IO.Path]::GetFullPath($DistributionDirectory)}else{Join-Path $root 'dist'}
if(!$UseExistingBuild){
    & (Join-Path $root 'build.ps1') -DistributionDirectory $dist
    & (Join-Path $PSScriptRoot 'OneClick.ps1') -DistributionDirectory $dist
}
$setup=Get-Item -LiteralPath (Join-Path $dist 'Ostum-Desktop-Setup.exe')
$app=Get-Item -LiteralPath (Join-Path $root '.build/OstumDesktop.exe')
[xml]$wix=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Product.wxs') -Raw -Encoding utf8
$version=[version]$app.VersionInfo.FileVersion
if($version.ToString(3) -ne $wix.Wix.Product.Version -or $setup.VersionInfo.FileVersion -ne $version.ToString()){
    throw 'Application, MSI and one-click installer versions must match.'
}
if($UseExistingBuild){
    $check=Start-Process -FilePath $setup.FullName -ArgumentList '--verify' -WindowStyle Hidden -PassThru
    $check.WaitForExit()
    if($check.ExitCode){throw 'Installer payload verification failed.'}
}
$checksum=(Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'),"$checksum  $($setup.Name)`n",[Text.Encoding]::ASCII)
Write-Output ("Ostum Desktop {0}, {1:N0} bytes, SHA-256 {2}" -f $version.ToString(3),$setup.Length,$checksum)
