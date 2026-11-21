param([Parameter(Mandatory)][string]$Kit,[Parameter(Mandatory)][string]$Out,[string]$RefreshSetup)
$ErrorActionPreference='Stop'
$folder=(Get-Item -LiteralPath $Kit).FullName
$manifest=Join-Path $folder 'SHA256SUMS.txt'
$lines=@([IO.File]::ReadAllLines($manifest))
if($RefreshSetup){
    $source=(Get-Item -LiteralPath $RefreshSetup).FullName
    $expected=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
    $index=@(for($i=0;$i -lt $lines.Count;$i++){if($lines[$i] -match '^[0-9a-f]{64}  Ostum-Desktop-Setup\.exe$'){$i}})
    if($index.Count -ne 1){throw 'Missing or duplicate installer in manifest'}
    $lines[$index[0]]=$expected+'  Ostum-Desktop-Setup.exe'
    $tmp=Join-Path $folder ('.verify-'+[guid]::NewGuid().ToString('N')+'.tmp')
    try{
        Copy-Item -LiteralPath $source -Destination $tmp
        if((Get-FileHash -LiteralPath $tmp).Hash -ne $expected){throw 'Installer copy did not match'}
        [IO.File]::Replace($tmp,(Join-Path $folder 'Ostum-Desktop-Setup.exe'),[NullString]::Value)
        [IO.File]::WriteAllLines($tmp,$lines,[Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($tmp,$manifest,[NullString]::Value)
    }finally{if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp}}
}
$checks=@(foreach($line in $lines){
    if($line -notmatch '^([a-fA-F0-9]{64})  (.+)$'){throw 'Invalid manifest line'}
    $expected=$matches[1];$name=$matches[2]
    $path=[IO.Path]::GetFullPath((Join-Path $folder $name))
    if(!$path.StartsWith($folder.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Entry outside the kit'}
    $file=Get-Item -LiteralPath $path
    [pscustomobject]@{file=$name;bytes=$file.Length;correct=(Get-FileHash -LiteralPath $file.FullName).Hash -eq $expected}
})
$checks|ConvertTo-Json|Set-Content -LiteralPath $Out -Encoding utf8
if(!$checks.Count -or @($checks|Where-Object {!$_.correct}).Count){throw 'Offline kit verification failed'}
Write-Output ("Verified "+$checks.Count+" files")
