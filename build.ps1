param([string]$RuntimeCache, [switch]$AppOnly)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$build = Join-Path $root '.build'
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
foreach ($dir in @($build,$dist)) { [void][IO.Directory]::CreateDirectory($dir) }
if (!$RuntimeCache) { $RuntimeCache = Join-Path $build 'runtime' }
& (Join-Path $root 'packaging\Icon.ps1') -OutputFolder $build
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$refs = @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Web.dll','System.Data.dll','System.Xml.dll','System.Xml.Linq.dll','System.IO.Compression.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll','WPF\WindowsBase.dll')
$options = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:'+(Join-Path $build 'OstumDesktop.exe')),('/win32icon:'+(Join-Path $build 'ostum.ico')),('/win32manifest:'+(Join-Path $root 'packaging\app.manifest')))
foreach ($ref in $refs) { $options += '/r:'+(Join-Path $fx $ref) }
$options += '/r:'+(Join-Path $fx 'System.Drawing.dll')
$options += '/r:'+(Join-Path $fx 'netstandard.dll')
$speech=Get-ChildItem (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Recurse -Filter 'System.Speech.dll' | Select-Object -First 1
$options += '/r:'+$speech.FullName
$libraries=@(& (Join-Path $root 'packaging\Libraries.ps1') -Build $build)
foreach ($file in $libraries) { $options += '/r:'+$file }
foreach ($item in @('MainWindow.xaml','ModelsWindow.xaml','catalog.json','RoutePrompt.txt','AgentLights.png')) { $options += '/resource:'+(Join-Path $src $item)+','+$item }
$options += '/resource:'+(Join-Path $build 'icon.b64')+',icon.b64'
$files = @(Get-ChildItem -LiteralPath $src -Filter '*.cs' | ForEach-Object FullName)
& (Join-Path $fx 'csc.exe') @options @files
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
Copy-Item -LiteralPath (Join-Path $src 'OstumDesktop.exe.config') -Destination $build -Force
if ($AppOnly) { return }
& (Join-Path $root 'packaging\VoiceRuntime.ps1') -Build $build

function Download-File([string]$url,[string]$path,[string]$sha) {
    if (Test-Path -LiteralPath $path) {
        if (!$sha -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sha) {return}
    }
    $partial=$path+'.part'
    & curl.exe --fail --location --silent --show-error --retry 3 --output $partial $url
    if ($LASTEXITCODE -ne 0) { throw ('Download failed: '+$url) }
    if ($sha -and (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $sha) {throw ('Unexpected SHA-256: '+$url)}
    if (Test-Path -LiteralPath $path) {[IO.File]::Replace($partial,$path,$null)} else {[IO.File]::Move($partial,$path)}
}
$wix = Join-Path $build 'wix'
if (!(Test-Path -LiteralPath (Join-Path $wix 'light.exe'))) {
    $archive = Join-Path $build 'wix314.zip'
    Download-File 'https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip' $archive ''
    [void][IO.Directory]::CreateDirectory($wix)
    & tar.exe -xf $archive -C $wix
    if ($LASTEXITCODE -ne 0) { throw 'WiX extraction failed.' }
    Remove-Item -LiteralPath $archive
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
& (Join-Path $root 'packaging\NativeRuntime.ps1') -Build $build
[void][IO.Directory]::CreateDirectory($RuntimeCache)
$engines = @(
    @{zip='llama'; folder='llama-b12547'; dir='LlamaDir'; url='https://github.com/ggml-org/llama.cpp/releases/download/b12547/llama-b12547-bin-win-vulkan-x64.zip'; sha='2d381405d87d1163b1a1efafea5f4a22006660ab2530ebe9db1362b6f6531ab4'},
    @{zip='sd'; folder='sd-532d2c1'; dir='SdDir'; url='https://github.com/leejet/stable-diffusion.cpp/releases/download/master-1106-532d2c1/sd-master-532d2c1-bin-win-vulkan-x64.zip'; sha='3e66222c87ca27074e1fd8ce79706eef17a8aaf39949906aa1bee7e060f22ecc'}
)
$groups = @{}
foreach ($engine in $engines) {
    $zipFile = Join-Path $RuntimeCache ($engine.zip+'.zip')
    Download-File $engine.url $zipFile $engine.sha
    $target = Join-Path $RuntimeCache $engine.folder
    [void][IO.Directory]::CreateDirectory($target)
    $archive = [IO.Compression.ZipFile]::OpenRead($zipFile)
    $group = @()
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.Name -notmatch '^(llama-server|sd-cli)\.exe$|\.dll$|\.txt$|^LICENSE' -or $entry.Name -match '^llama-(batched|completion|quantize|cli-|perplexity|fit-params|imatrix|bench)') { continue }
            $targetFile = Join-Path $target $entry.Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$targetFile,$true)
            $group += Get-Item -LiteralPath $targetFile
        }
    } finally { $archive.Dispose() }
    $groups[$engine.dir] = $group
}
$groups['NoticeDir'] = @(Get-ChildItem -LiteralPath (Join-Path $root 'packaging\licenses') -File) + @(Get-Item -LiteralPath (Join-Path $root 'packaging\Sources.txt'))
$fragment = Join-Path $build 'Runtime.wxs'
$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$writer = [Xml.XmlWriter]::Create($fragment,$settings)
$ns = 'http://schemas.microsoft.com/wix/2006/wi'
$componentIds = @()
$hash = [Security.Cryptography.SHA256]::Create()
try {
    $writer.WriteStartElement('Wix',$ns)
    $writer.WriteStartElement('Fragment',$ns)
    foreach ($dirId in $groups.Keys | Sort-Object) {
        $writer.WriteStartElement('DirectoryRef',$ns); $writer.WriteAttributeString('Id',$dirId)
        foreach ($file in $groups[$dirId] | Sort-Object Name) {
            $bytes = $hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($dirId + '/' + $file.Name))
            $guid = [Guid]::new([byte[]]$bytes[0..15]); $id = 'A' + $guid.ToString('N')
            $componentIds += $id
            $writer.WriteStartElement('Component',$ns); $writer.WriteAttributeString('Id',$id)
            $writer.WriteAttributeString('Guid',$guid.ToString()); $writer.WriteAttributeString('Win64','yes')
            $writer.WriteStartElement('File',$ns); $writer.WriteAttributeString('Id',('F'+$id.Substring(1)))
            $writer.WriteAttributeString('Source',$file.FullName); $writer.WriteAttributeString('KeyPath','yes')
            $writer.WriteEndElement(); $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    }
    $writer.WriteStartElement('ComponentGroup',$ns); $writer.WriteAttributeString('Id','RuntimeFiles')
    foreach ($id in $componentIds) {
        $writer.WriteStartElement('ComponentRef',$ns); $writer.WriteAttributeString('Id',$id); $writer.WriteEndElement()
    }
    $writer.WriteEndElement(); $writer.WriteEndElement(); $writer.WriteEndElement()
} finally { $writer.Dispose(); $hash.Dispose() }
$objects = @()
foreach ($wxs in @((Join-Path $root 'packaging\Product.wxs'),$fragment)) {
    $obj = Join-Path $build ([IO.Path]::GetFileNameWithoutExtension($wxs)+'.wixobj')
    & (Join-Path $wix 'candle.exe') -nologo -arch x64 ('-dBuild='+$build) ('-dSrc='+$src) -out $obj $wxs
    if ($LASTEXITCODE -ne 0) { throw 'WiX compilation failed.' }
    $objects += $obj
}
$msi = Join-Path $dist 'OstumDesktop.msi'
& (Join-Path $wix 'light.exe') -nologo -sval -spdb -cultures:ru-ru -ext WixUIExtension -out $msi @objects
if ($LASTEXITCODE -ne 0) { throw 'MSI linking failed.' }
Get-Item -LiteralPath $msi | Select-Object FullName,Length
