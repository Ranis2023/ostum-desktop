param([Parameter(Mandatory)][string]$Build)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root=Split-Path -Parent $PSScriptRoot
$cache=Join-Path $Build 'pro5/incoming'
$toolsDir=Join-Path $Build 'pro5/tools'
foreach($p in @($cache,$toolsDir)){[void][IO.Directory]::CreateDirectory($p)}
function Get-Pinned($name,$url,$sha){
    $path=Join-Path $cache $name
    if(!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $sha){
        $partial=$path+'.download'
        & curl.exe --fail --location --retry 3 --silent --show-error --output $partial $url
        if($LASTEXITCODE -or (Get-FileHash -LiteralPath $partial).Hash -ne $sha){throw "Invalid component $name"}
        Move-Item -LiteralPath $partial -Destination $path -Force
    }
    return $path
}
$py=Get-Pinned 'python314.zip' 'https://www.python.org/ftp/python/3.14.10/python-3.14.10-embed-amd64.zip' '90905E64A840557D8C1BC4F5B74DB3D7EE85E3FFB217611CAC1C7813ACEA4159'
$np=Get-Pinned 'numpy.whl' 'https://files.pythonhosted.org/packages/74/e2/494516771374b43060c1b778b371cba53d576abd3aee1275eecd73896e3b/numpy-2.5.5-cp314-cp314-win_amd64.whl' '20145B11776D2F7570CD61351B156A51D3A341822282095BAA819D87272BC710'
$msi=Get-Pinned 'espeak.msi' 'https://github.com/espeak-ng/espeak-ng/releases/download/1.52.0/espeak-ng.msi' '7F673C709EA5DD579D3B5EBB98688CC575328A6AB7438D2BC405B88CEDAEAFB9'
$source=Get-Pinned 'espeak-source.zip' 'https://codeload.github.com/espeak-ng/espeak-ng/zip/refs/tags/1.52.0' 'B4517592E3CBC43703BB1C782702EAFB98097659E0B7E96E69C32EE32AE5003A'
$python=Join-Path $toolsDir 'python'
if(!(Test-Path -LiteralPath (Join-Path $python 'numpy/__init__.py'))){
    [void][IO.Directory]::CreateDirectory($python)
    foreach($archive in @($py,$np)){
        & tar.exe -xf $archive -C $python
        if($LASTEXITCODE){throw 'Failed to extract Python runtime.'}
    }
}
$extracted=Join-Path $Build 'pro5/espeak-extracted'
$speech=Join-Path $extracted 'eSpeak NG'
if(!(Test-Path -LiteralPath (Join-Path $speech 'espeak-ng.exe'))){
    $job=Start-Process -FilePath "$env:SystemRoot/System32/msiexec.exe" -ArgumentList ('/a "'+$msi+'" /qn TARGETDIR="'+$extracted+'"') -WindowStyle Hidden -PassThru -Wait
    if($job.ExitCode){throw 'Failed to extract speech engine.'}
}
$dst=Join-Path $toolsDir 'speech'
if(!(Test-Path -LiteralPath (Join-Path $dst 'espeak-ng.exe'))){Copy-Item -LiteralPath $speech -Destination $dst -Recurse}
Copy-Item -LiteralPath $source -Destination (Join-Path $dst 'corresponding-source.zip') -Force
$training=Join-Path $toolsDir 'training'
[void][IO.Directory]::CreateDirectory($training)
foreach($name in @('studio.py','bench.py','tune.py')){Copy-Item -LiteralPath (Join-Path $root "training/$name") -Destination $training -Force}
Copy-Item -LiteralPath (Join-Path $root 'src/office.py') -Destination $toolsDir -Force
$archive=Join-Path $Build 'OstumTools.zip'
$next=$archive+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
try{
    $zip=[IO.Compression.ZipFile]::Open($next,[IO.Compression.ZipArchiveMode]::Create)
    $level=[IO.Compression.CompressionLevel]::Fastest
    try {
        # Read Python and NumPy directly from the pinned distributions, independently of local bytecode caches.
        foreach($distribution in @($py,$np)){
            $package=[IO.Compression.ZipFile]::OpenRead($distribution)
            try{
                foreach($member in $package.Entries){
                    if(!$member.Name){continue}
                    $entry=$zip.CreateEntry(('python/'+$member.FullName),$level)
                    $entry.LastWriteTime=$member.LastWriteTime
                    $inputStream=$member.Open()
                    try{$outputStream=$entry.Open();try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose()}}
                    finally{$inputStream.Dispose()}
                }
            }finally{$package.Dispose()}
        }
        foreach($file in (Get-ChildItem -LiteralPath $speech -Recurse -File | Sort-Object FullName)){
            $name='speech/'+$file.FullName.Substring($speech.Length+1).Replace('\','/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$name,$level)
        }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$source,'speech/corresponding-source.zip',$level)
        foreach($name in @('office.py','training/studio.py','training/bench.py','training/tune.py')){
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $toolsDir $name),$name,$level)
        }
    }finally{$zip.Dispose()}
    Move-Item -LiteralPath $next -Destination $archive -Force
    [IO.File]::WriteAllText($archive+'.sha256',(Get-FileHash -LiteralPath $archive).Hash)
}finally{if(Test-Path -LiteralPath $next){Remove-Item -LiteralPath $next}}
