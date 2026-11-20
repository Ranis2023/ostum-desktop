param([Parameter(Mandatory=$true)][string]$Build)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages=@(
    @('naudio.core','2.2.1','netstandard2.0','794645DBFD30E4880663D52D9D9224D55C301FA54228FF623C95D572C7887347'),
    @('naudio.wasapi','2.2.1','netstandard2.0','C8396B8DBAB86A2619EDFD9158BEAF18C21CB86677099B5594950FD8651B9D1A'),
    @('pdfpig','0.1.16','net471','D67171846EA8C28F50359137065FEC4514266D7A32B23EAE6C5F2EBED8FFCFC4'),
    @('microsoft.bcl.hashcode','6.0.0','net462','F3B9B2BAB0BF8CC717D5FDF6D7AEE3EC54E36D9E85BD41347ACAE161319CBD6B'),
    @('system.memory','4.6.0','net462','3A10042B350CE9E11A1FDF4370668CCF6A452C6FEAFCDE0A556AAA881614385A'),
    @('system.buffers','4.6.0','net462','736425805075E8895F066B3960BB1308543F41E2A84BAA61D5AF6675192AFC97'),
    @('system.numerics.vectors','4.6.0','net462','7CA4B7B964361E647AF6F3610C7A8F2D834EB77AA98E25903976431EF444D475'),
    @('system.runtime.compilerservices.unsafe','6.1.0','net462','372AAAA51707BA6CD2C69B2044382E0F9486C1D50D1C16E8D0E39DCCB4C8CC25')
)
foreach($pkg in $packages){
    $id,$v,$framework,$hash=$pkg
    $path=Join-Path $Build ($id+'.nupkg')
    if(!(Test-Path -LiteralPath $path)){
        & curl.exe -fsSL --retry 3 -o ($path+'.part') ('https://api.nuget.org/v3-flatcontainer/'+$id+'/'+$v+'/'+$id+'.'+$v+'.nupkg')
        if($LASTEXITCODE -ne 0){throw ('Could not download '+$id)}
        if((Get-FileHash -LiteralPath ($path+'.part')).Hash -ne $hash){throw ('Checksum mismatch '+$id)}
        [IO.File]::Move($path+'.part',$path)
    }
    if((Get-FileHash -LiteralPath $path).Hash -ne $hash){throw ('Checksum mismatch '+$id)}
    $zip=[IO.Compression.ZipFile]::OpenRead($path)
    try {
        foreach($entry in $zip.Entries|Where-Object {$_.FullName -like ('lib/'+$framework+'/*.dll') -and $_.Name -notlike '*.Package.dll'}){
            $file=Join-Path $Build $entry.Name
            if(!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne $entry.Length){
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$file,$true)
            }
            $file
        }
    } finally {$zip.Dispose()}
}
