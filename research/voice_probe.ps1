param([Parameter(Mandatory)][string]$Out)
$ErrorActionPreference='Stop'
$directory=[IO.Path]::GetFullPath($Out)
[void][IO.Directory]::CreateDirectory($directory)
$appDir=Join-Path $env:LOCALAPPDATA 'Programs/Ostum Desktop'
Add-Type -AssemblyName System.Speech
[void][Reflection.Assembly]::LoadFrom((Join-Path $appDir 'OstumDesktop.exe'))
$audio=Join-Path $directory 'synthetic.wav'
$speaker=[Sfera.Voice]::RussianSpeaker()
try{
    $format=New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000,[System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,[System.Speech.AudioFormat.AudioChannel]::Mono)
    $speaker.SetOutputToWaveFile($audio,$format)
    $speaker.Speak('Подготовь презентацию для встречи с клиентом.')
    $name=$speaker.Voice.Name
}finally{$speaker.Dispose()}
$srv=[Sfera.Voice]::new((Join-Path $appDir 'engines'))
try{
    $modelPath=[Sfera.Store]::Load().Models
    $transcript=$srv.Recognize($audio,$modelPath,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $ok=$transcript -match 'презентаци' -and $transcript -match 'клиент'
    $result=[ordered]@{speaker=$name;synthesized_speech=$true;recognized=$transcript;passed=$ok}
    [IO.File]::WriteAllText((Join-Path $directory 'result.json'),($result|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    if(!$ok){throw ('Local voice recognition failed: '+$transcript)}
}finally{$srv.Dispose()}
