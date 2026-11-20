param([string]$Kit,[string]$Image)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(!$Kit){$Kit=Join-Path $root 'dist/Ostum-Pro-Offline'}
$Kit=[IO.Path]::GetFullPath($Kit)
$setup=Get-Item -LiteralPath (Join-Path $Kit 'Ostum-Desktop-Setup.exe')
$kitVersion=([version]$setup.VersionInfo.FileVersion).ToString(3)
if(!$Image){$Image=Join-Path $root ('dist/OSTUM-Desktop-'+$kitVersion+'-Offline.iso')}
$Image=[IO.Path]::GetFullPath($Image)
if($Image.StartsWith($Kit.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Place the image outside the source tree.'}
$cat=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'src/catalog.json')|ConvertFrom-Json
$all=@($setup)+@($cat.files|ForEach-Object {Get-Item -LiteralPath (Join-Path $Kit ('Models/'+$_.name))})+@($cat.acceleration.libraries|ForEach-Object {Get-Item -LiteralPath (Join-Path $Kit ('Acceleration/'+$_.name))})
$lines=@()
foreach($file in $all){
    $hash=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    $spec=@($cat.files)+@($cat.acceleration.libraries)|Where-Object name -eq $file.Name|Select-Object -First 1
    if($spec -and $hash -ne $spec.sha256){throw "Checksum mismatch $($file.Name)"}
    $lines+="$hash  "+$file.FullName.Substring($Kit.Length+1).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $Kit 'SHA256SUMS.txt'),[string[]]$lines)
[IO.File]::WriteAllText((Join-Path $Kit 'OSTUM-OFFLINE.txt'),"OSTUM Desktop $kitVersion · Запустите Ostum-Desktop-Setup.exe. Установка с этого носителя работает без сети.",[Text.UTF8Encoding]::new($true))
$fs=New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$fs.FileSystemsToCreate=4
$fs.FreeMediaBlocks=21000000
$fs.UDFRevision=0x0201
$fs.VolumeName='OSTUM_PRO_5'
$fs.Root.AddTree($Kit,$false)
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class OstumImageWriter {
    public static void Save(object source,string path) {
        IStream stream=(IStream)source;
        byte[] buf=new byte[2*1024*1024];
        IntPtr count=Marshal.AllocHGlobal(4);
        try {
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                while(true) {
                    stream.Read(buf,buf.Length,count);
                    int n=Marshal.ReadInt32(count);
                    if(n==0)break;
                    output.Write(buf,0,n);
                }
                output.Flush(true);
            }
        } finally { Marshal.FreeHGlobal(count); }
    }
}
'@
$result=$fs.CreateResultImage()
$stage=$Image+'.'+[Guid]::NewGuid().ToString('N')+'.preparing'
try{
    [OstumImageWriter]::Save($result.ImageStream,$stage)
    $expected=[int64]$result.TotalBlocks*$result.BlockSize
    if((Get-Item -LiteralPath $stage).Length -ne $expected){throw 'Incomplete disc image.'}
    Move-Item -LiteralPath $stage -Destination $Image -Force
    [IO.File]::WriteAllText($Image+'.sha256',(Get-FileHash -LiteralPath $Image).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($Image))
}finally{if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage}}
Get-Item -LiteralPath $Image|Select-Object FullName,Length
