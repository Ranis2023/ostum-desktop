param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference='Stop'
$audit=[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Out)).FullName
$catalog=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/model-catalog.json') -Raw -Encoding utf8|ConvertFrom-Json
$rows=foreach($model in $catalog){
    if($model.builtin){continue}
    $repo=([uri]$model.source).AbsolutePath.Trim('/')
    $meta=Invoke-RestMethod -Uri ('https://huggingface.co/api/models/'+$repo+'/revision/'+$model.revision+'?blobs=true') -TimeoutSec 30
    foreach($file in $model.files){
        $entry=$meta.siblings|Where-Object rfilename -eq $file.name|Select-Object -First 1
        $header=Join-Path $audit ($model.revision+'-'+$file.name+'.header')
        & curl.exe -sSfL --range 0-15 --max-filesize 256 --retry 1 --max-time 28 --output $header ($file.url+'?ostum-catalog-header=16')
        $magic=if($LASTEXITCODE -eq 0 -and (Get-Item -LiteralPath $header).Length -eq 16){[Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($header),0,4)}else{''}
        [pscustomobject]@{model=$model.title;file=$file.name;verified=($magic -eq 'GGUF' -and $entry.lfs.sha256 -eq $file.sha256 -and $entry.size -eq $file.bytes);magic=$magic;bytes=$file.bytes}
    }
}
[IO.File]::WriteAllText((Join-Path $audit 'catalog.json'),(ConvertTo-Json -InputObject @($rows)),[Text.UTF8Encoding]::new($false))
Write-Output ("Catalog: {0}/{1} remote headers, byte counts and declared SHA-256" -f @($rows|Where-Object verified).Count,@($rows).Count)
