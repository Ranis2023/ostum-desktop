param([Parameter(Mandatory)][string]$Build)
$ErrorActionPreference='Stop'
$dir=Join-Path $Build 'voice'
$engine=Join-Path $dir 'engine'
[void][IO.Directory]::CreateDirectory($engine)
$archive=Join-Path $dir 'whisper.zip'
$sha='6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb'
if(!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive).Hash -ne $sha){
    & curl.exe -fLsS --retry 3 -o ($archive+'.part') 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip'
    if($LASTEXITCODE -or (Get-FileHash -LiteralPath ($archive+'.part')).Hash -ne $sha){throw 'Invalid whisper.cpp archive'}
    Move-Item -LiteralPath ($archive+'.part') -Destination $archive -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{foreach($entry in $zip.Entries){if($entry.Name -match '^(ggml.*\.dll|whisper\.dll|whisper-cli\.exe)$'){[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $engine $entry.Name),$true)}}}finally{$zip.Dispose()}
