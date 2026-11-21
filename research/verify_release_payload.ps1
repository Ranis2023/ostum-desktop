param([Parameter(Mandatory)][string]$Distribution,[Parameter(Mandatory)][string]$Build,[Parameter(Mandatory)][string]$Out)
$ErrorActionPreference='Stop'
$wix=Join-Path (Split-Path $PSScriptRoot -Parent) '.build/wix'
foreach($name in 'Microsoft.Deployment.WindowsInstaller','Microsoft.Deployment.Compression','Microsoft.Deployment.Compression.Cab'){
    [void][Reflection.Assembly]::LoadFrom((Join-Path $wix ($name+'.dll')))
}
$msi=Get-Item -LiteralPath (Join-Path $Distribution 'OstumDesktop.msi')
$expected=[ordered]@{SferaExecutable='OstumDesktop.exe';SferaConfig='OstumDesktop.exe.config';OstumTools='OstumTools.zip';OstumToolsDigest='OstumTools.zip.sha256';HelpFile='OSTUM-Help.html'}
$db=[Microsoft.Deployment.WindowsInstaller.Database]::new($msi.FullName,[Microsoft.Deployment.WindowsInstaller.DatabaseOpenMode]::ReadOnly)
$checks=@()
try{
    $media=$db.OpenView('SELECT LastSequence,Cabinet FROM Media ORDER BY DiskId')
    $media.Execute()
    try{
        $start=0
        while($null -ne ($row=$media.Fetch())){
            try{$end=$row.GetInteger(1);$cabinet=$row.GetString(2).TrimStart('#')}finally{$row.Dispose()}
            $members=@($expected.Keys|Where-Object {
                $seq=[int]$db.ExecuteScalar("SELECT Sequence FROM File WHERE File = '$_'",[object[]]@())
                $seq -gt $start -and $seq -le $end
            })
            $start=$end
            if(!$members.Count){continue}
            $view=$db.OpenView("SELECT Data FROM _Streams WHERE Name = '$cabinet'")
            $memory=New-Object IO.MemoryStream
            try{
                $view.Execute();$row=$view.Fetch()
                try{$stream=$row.GetStream(1);try{$stream.CopyTo($memory)}finally{$stream.Dispose()}}finally{$row.Dispose()}
                $engine=[Microsoft.Deployment.Compression.Cab.CabEngine]::new()
                try{
                    foreach($key in $members){
                        $memory.Position=0;$file=$engine.Unpack($memory,[string]$key)
                        try{$sha=[Security.Cryptography.SHA256]::Create();try{$digest=[BitConverter]::ToString($sha.ComputeHash($file)).Replace('-','')}finally{$sha.Dispose()}}finally{$file.Dispose()}
                        $name=$expected[$key];$gold=(Get-FileHash -LiteralPath (Join-Path $Build $name)).Hash
                        $checks+=[pscustomobject]@{file=$name;sha256=$digest;passed=$digest -eq $gold}
                    }
                }finally{$engine.Dispose()}
            }finally{$memory.Dispose();$view.Dispose()}
        }
    }finally{$media.Dispose()}
}finally{$db.Dispose()}
if($checks.Count -ne $expected.Count -or @($checks|Where-Object {!$_.passed}).Count){throw 'MSI payload differs from the verified application build.'}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out),($checks|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
"Verified $($checks.Count) packaged files."
