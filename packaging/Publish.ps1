param([string]$GitHubCli='gh',[string]$NotesFile)
$ErrorActionPreference='Stop'
$repo='Ranis2023/ostum-desktop'
$root=Split-Path -Parent $PSScriptRoot
$exe=Get-Item -LiteralPath (Join-Path $root 'dist/Ostum-Desktop-Setup.exe')
$sum=Get-Item -LiteralPath (Join-Path $root 'dist/SHA256SUMS.txt')
$ver=([version]$exe.VersionInfo.FileVersion).ToString(3)
$tag='v'+$ver
if(!$NotesFile){$NotesFile=Join-Path $PSScriptRoot ('releases/'+$ver+'.md')}
if(!(Test-Path -LiteralPath $NotesFile)){throw "Create release notes for $ver."}
$hash=(Get-FileHash -LiteralPath $exe.FullName).Hash.ToLowerInvariant()
if(([IO.File]::ReadAllText($sum.FullName)).Trim() -cne "$hash  $($exe.Name)"){throw 'Installer SHA-256 differs from SHA256SUMS.txt.'}
$head=(& git -C $root rev-parse HEAD).Trim()
$published=& $GitHubCli api "repos/$repo/commits/main" --jq .sha
if($LASTEXITCODE -or $published -ne $head){throw 'Publish the exact source revision to main before creating its release.'}
$tagRows=@(& git -C $root ls-remote --tags "https://github.com/$repo.git" "refs/tags/$tag" "refs/tags/$tag^{}")
if($LASTEXITCODE){throw 'Could not check the release tag.'}
if($tagRows.Count -gt 0 -and !(($tagRows|ForEach-Object {($_ -split '\s+')[0]}) -contains $head)){throw 'The release tag points to a different commit.'}
$existing=& $GitHubCli release list --repo $repo --limit 100 --json tagName,isDraft | ConvertFrom-Json | Where-Object tagName -eq $tag | Select-Object -First 1
if($LASTEXITCODE){throw 'Could not inspect releases.'}
if($existing -and !$existing.isDraft){throw "Release $tag is already published."}
if($existing){
    $prior=& $GitHubCli release view $tag --repo $repo --json targetCommitish | ConvertFrom-Json
    if($LASTEXITCODE -or $prior.targetCommitish -ne $head){throw 'A draft already exists for another commit.'}
}
if(!$existing){
    & $GitHubCli release create $tag --draft --target $head --repo $repo --title "OSTUM Desktop $ver" --notes-file $NotesFile
    if($LASTEXITCODE){throw 'Could not create the draft release.'}
}
& $GitHubCli release upload $tag $exe.FullName $sum.FullName --repo $repo --clobber
if($LASTEXITCODE){throw 'Asset upload failed. The draft can be resumed.'}
$draft=& $GitHubCli release view $tag --repo $repo --json databaseId,isDraft,targetCommitish | ConvertFrom-Json
if($LASTEXITCODE -or !$draft.isDraft -or $draft.targetCommitish -ne $head){throw 'Draft target changed.'}
$assets=@(& $GitHubCli api "repos/$repo/releases/$($draft.databaseId)/assets" | ConvertFrom-Json)
if($LASTEXITCODE){throw 'Could not read the uploaded assets.'}
foreach($file in @($exe,$sum)){
    $asset=$assets|Where-Object name -eq $file.Name|Select-Object -First 1
    $expected='sha256:'+(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    if(!$asset -or $asset.size -ne $file.Length -or $asset.digest -ne $expected){throw "Asset verification failed: $($file.Name)"}
}
& $GitHubCli release edit $tag --repo $repo --draft=false --latest
if($LASTEXITCODE){throw 'Draft publication failed.'}
