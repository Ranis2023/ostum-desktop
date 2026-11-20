[CmdletBinding()]
param([ValidateSet('Initialize','Configure','Resume','Run','Check','HandleUpdate')][string]$Mode='Run',[switch]$Graphical)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Desktop'){$env:PSModulePath=(Join-Path $PSHOME 'Modules')+';'+$env:PSModulePath}
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Net.Http
$channel='@ranisgaliaskarov'
$privateDir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'OSTUM-Subscription-Issuer'
$keyFile=Join-Path $privateDir 'signing.dpapi'
$configFile=Join-Path $privateDir 'telegram.dpapi'
$pendingFile=Join-Path $privateDir 'pending-telegram.dpapi'
$pendingChoice=Join-Path $privateDir 'pending-receiver.txt'
$offsetFile=Join-Path $privateDir 'offset.txt'
$publicFile=Join-Path $PSScriptRoot 'issuer-public.xml'
$receiverFile=Join-Path $privateDir 'receiver.txt'
$utf8=New-Object Text.UTF8Encoding($false)

function Save-Bytes([string]$Path,[byte[]]$Data) {
    $temp=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        [IO.File]::WriteAllBytes($temp,$Data)
        if([IO.File]::Exists($Path)){[IO.File]::Replace($temp,$Path,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
    }finally{if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}}
}
function Save-Private([string]$Path,[string]$Value) {
    $bytes=[Security.Cryptography.ProtectedData]::Protect($utf8.GetBytes($Value),$null,'CurrentUser'); Save-Bytes $Path $bytes
}
function Read-Private([string]$Path) {
    $utf8.GetString([Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Path),$null,'CurrentUser'))
}
function New-MaskedSecret([string]$Text) {
    $secure=[Security.SecureString]::new()
    for($i=0;$i -lt $Text.Length;$i++){$secure.AppendChar($Text[$i])}
    $secure.MakeReadOnly();return $secure
}
function Signature([string]$Body) {
    $bytes=$script:rsa.SignData([Text.Encoding]::ASCII.GetBytes($Body),'SHA256')
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}
function Service-Event([string]$Name) {
    Save-Bytes (Join-Path $privateDir ($Name+'.utc')) $utf8.GetBytes([DateTime]::UtcNow.ToString('o'))
}
function Telegram([string]$Method,[hashtable]$Body) {
    try {
        $json=$Body|ConvertTo-Json -Compress -Depth 8
        $result=Invoke-RestMethod -Method Post -Uri ($script:api+$Method) -ContentType 'application/json; charset=utf-8' -Body $utf8.GetBytes($json) -TimeoutSec 55
        if(-not $result.ok){throw 'Telegram declined the request'}
        return $result.result
    }catch {
        $response=$_.Exception.Response
        if($response -and [int]$response.StatusCode -eq 401){throw 'Telegram отклонил токен. Скопируйте актуальный API Token для @rangoostum_bot у BotFather.'}
        if($response -and [int]$response.StatusCode -eq 429){throw 'Telegram временно ограничил частоту запросов. Повторите проверку через минуту.'}
        throw "Telegram не подтвердил операцию $Method. Проверьте сеть и назначение @rangoostum_bot администратором @ranisgaliaskarov."
    }
}
function Connect-Channel([string]$Token) {
    if($Token -cnotmatch '^[0-9]+:[A-Za-z0-9_-]{20,}$'){throw 'Формат токена BotFather неверен.'}
    $script:api='https://api.telegram.org/bot'+$Token+'/'
    $me=Telegram 'getMe' @{}
    if(-not $me.is_bot -or $me.username -ne 'rangoostum_bot'){throw 'Введите токен именно бота @rangoostum_bot.'}
    $chat=Telegram 'getChat' @{chat_id=$channel}
    if($chat.type -ne 'channel' -or ('@'+$chat.username) -ne $channel){throw 'Проверьте адрес канала.'}
    $self=Telegram 'getChatMember' @{chat_id=$chat.id;user_id=$me.id}
    if($self.status -notin @('creator','administrator')){throw 'Назначьте бота администратором канала и повторите настройку.'}
    $script:channelId=$chat.id; $script:username=$me.username; $script:botId=$me.id
}
function Send-Text($Chat,[string]$Text,$Markup=$null) {
    $body=@{chat_id=$Chat;text=$Text};if($Markup){$body.reply_markup=$Markup};$null=Telegram 'sendMessage' $body
}
function Test-Subscriber($Member,$UserId) {
    return ($null -ne $Member -and $Member.user.id -eq $UserId -and ($Member.status -in @('creator','administrator','member') -or ($Member.status -eq 'restricted' -and $Member.is_member -eq $true)))
}
function Send-Grant($Chat,[string]$Device) {
    $issued=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();$end=$issued+604800
    $body='OP1.'+$Device+'.'+$issued.ToString([Globalization.CultureInfo]::InvariantCulture)+'.'+$end.ToString([Globalization.CultureInfo]::InvariantCulture)
    $receipt=$body+'.'+(Signature $body)
    $form=[Net.Http.MultipartFormDataContent]::new()
    try {
        $form.Add([Net.Http.StringContent]::new([string]$Chat),'chat_id')
        $form.Add([Net.Http.StringContent]::new('Подписка подтверждена. Сохраните файл и перетащите его в окно «Доступ к OSTUM Pro». Доступ действует семь дней.'),'caption')
        $form.Add([Net.Http.StringContent]::new($receipt,[Text.Encoding]::ASCII,'application/octet-stream'),'document','OSTUM-Pro.ostumpro')
        $res=$script:http.PostAsync($script:api+'sendDocument',$form).GetAwaiter().GetResult()
        try {if(-not $res.IsSuccessStatusCode -or -not (($res.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json).ok)){throw 'delivery failed'}}finally{$res.Dispose()}
    }catch{throw 'Не удалось доставить файл через Telegram.'}finally{$form.Dispose()}
}
function Handle-Update($Update) {
    if($Update.callback_query){
        $q=$Update.callback_query;$msg=$q.message;$actor=$q.from;$request=$q.data
        try{$null=Telegram 'answerCallbackQuery' @{callback_query_id=$q.id}}catch{}
    }else{$msg=$Update.message;$actor=$msg.from;$request=$msg.text}
    if(-not $msg -or $msg.chat.type -ne 'private' -or $actor.is_bot -or $msg.chat.id -ne $actor.id){return}
    if($request -cnotmatch '^(?:/start\s+)?p_([A-Za-z0-9_-]{24})$'){
        Send-Text $actor.id 'В OSTUM откройте «Рабочие пространства Pro», нажмите «Проверить подписку» или скопируйте запрос из окна и пришлите его сюда.';return
    }
    $device=$Matches[1]
    try{
        $admin=Telegram 'getChatMember' @{chat_id=$script:channelId;user_id=$script:botId}
        if($admin.user.id -ne $script:botId -or $admin.status -notin @('administrator','creator')){throw 'Administrator rights required'}
        $member=Telegram 'getChatMember' @{chat_id=$script:channelId;user_id=$actor.id}
    }catch{
        Send-Text $actor.id 'Telegram временно не подтвердил статус. Повторите запрос чуть позже.';return
    }
    if(Test-Subscriber $member $actor.id){Send-Grant $actor.id $device;Service-Event 'last-issue';return}
    $buttons=@{inline_keyboard=@(,@(@{text='Открыть канал';url='https://t.me/ranisgaliaskarov'},@{text='Я подписался · проверить';callback_data='p_'+$device}))}
    Send-Text $actor.id 'Подпишитесь на канал Раниса Галиаскарова и нажмите кнопку проверки.' $buttons
}

function Read-SetupForm {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $win=[Windows.Forms.Form]::new();$win.Text='OSTUM · Подключение @rangoostum_bot';$win.ClientSize=[Drawing.Size]::new(516,312);$win.StartPosition='CenterScreen';$win.FormBorderStyle='FixedDialog';$win.MaximizeBox=$false;$win.Font=[Drawing.Font]::new('Segoe UI',10);$win.TopMost=$true
    $label=[Windows.Forms.Label]::new();$label.SetBounds(18,16,480,70);$label.Text="Вставьте токен @rangoostum_bot из BotFather. OSTUM проверит его права в @ranisgaliaskarov и сохранит токен в защищённом хранилище этой учётной записи Windows.";$win.Controls.Add($label)
    $box=[Windows.Forms.TextBox]::new();$box.SetBounds(19,91,475,30);$box.UseSystemPasswordChar=$true;$win.Controls.Add($box)
    $saved=[IO.File]::Exists($pendingFile) -or [IO.File]::Exists($configFile)
    if($saved){$label.Text='Токен уже сохранён в защищённом хранилище Windows. Оставьте поле пустым для повторной проверки или вставьте новый токен @rangoostum_bot.'}
    $run=[Windows.Forms.CheckBox]::new();$run.SetBounds(19,137,472,58);$run.Text='Бот выделен для OSTUM. Запускать обработчик на этом компьютере сейчас и при входе в Windows.';$run.Checked=$true;$win.Controls.Add($run)
    $note=[Windows.Forms.Label]::new();$note.SetBounds(19,197,474,53);$note.Text='Если бот уже принимает заказы или обслуживает другой сервис, оставьте флажок пустым для подключения через действующий обработчик.';$win.Controls.Add($note)
    $ok=[Windows.Forms.Button]::new();$ok.Text='Проверить и сохранить';$ok.SetBounds(277,258,217,35);$ok.DialogResult='OK';$win.Controls.Add($ok);$win.AcceptButton=$ok
    $cancel=[Windows.Forms.Button]::new();$cancel.Text='Отмена';$cancel.SetBounds(165,258,102,35);$cancel.DialogResult='Cancel';$win.Controls.Add($cancel);$win.CancelButton=$cancel
    try{if($win.ShowDialog() -ne 'OK'){return $null};if(-not $box.Text.Trim() -and -not $saved){throw 'Токен не введён.'};return @{Secret=(New-MaskedSecret $box.Text.Trim());Standalone=$run.Checked}}finally{$box.Clear();$win.Dispose()}
}

if($MyInvocation.InvocationName -eq '.'){return}
[void][IO.Directory]::CreateDirectory($privateDir)
$csp=[Security.Cryptography.CspParameters]::new(24);$csp.Flags='CreateEphemeralKey'
$script:rsa=[Security.Cryptography.RSACryptoServiceProvider]::new(3072,$csp)
try {
    if(-not [IO.File]::Exists($keyFile)){
        if($Mode -ne 'Initialize' -or [IO.File]::Exists($publicFile)){throw 'Нужна исходная учётная запись издателя с ключом подписи.'}
        Save-Private $keyFile $script:rsa.ToXmlString($true)
    }else{$script:rsa.FromXmlString((Read-Private $keyFile))}
    $public=$script:rsa.ToXmlString($false)
    if([IO.File]::Exists($publicFile)){
        if([IO.File]::ReadAllText($publicFile).Trim() -cne $public){throw 'Ключ издателя отличается от ключа приложения.'}
    }elseif($Mode -eq 'Initialize'){Save-Bytes $publicFile $utf8.GetBytes($public)}else{throw 'Отсутствует открытый ключ приложения.'}
    if($Mode -eq 'Initialize'){Write-Output 'Открытый ключ готов. Закрытый ключ защищён Windows вне репозитория.';return}
    if($Mode -in @('Configure','Resume')){
        $installStartup=$Graphical.IsPresent
        if($Mode -eq 'Resume'){
            if(-not [IO.File]::Exists($pendingFile)){throw 'Нет отложенной настройки.'}
            $secret=New-MaskedSecret ''; $standalone=$true
            $installStartup=$false
            if([IO.File]::Exists($pendingChoice)){$choice=[IO.File]::ReadAllText($pendingChoice);$standalone=$choice -ne 'integrated';$installStartup=$choice -eq 'startup'}
        }elseif($Graphical){$formAnswer=Read-SetupForm;if(-not $formAnswer){return};$secret=$formAnswer.Secret;$standalone=$formAnswer.Standalone}
        else{$secret=Read-Host 'Токен BotFather (ввод скрыт)' -AsSecureString;$standalone=((Read-Host 'Бот выделен только для OSTUM? Для отдельного обработчика введите YES') -ceq 'YES')}
        $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
        try{
            $token=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
            if(-not $token){$existing=if([IO.File]::Exists($pendingFile)){$pendingFile}else{$configFile};$token=Read-Private $existing}
            if($token -cnotmatch '^[0-9]+:[A-Za-z0-9_-]{20,}$'){throw 'Проверьте формат токена BotFather. Вставьте его целиком.'}
            Save-Private $pendingFile $token
            $intent=if(-not $standalone){'integrated'}elseif($installStartup){'startup'}else{'standalone'};Save-Bytes $pendingChoice $utf8.GetBytes($intent)
            Connect-Channel $token;$webhook=Telegram 'getWebhookInfo' @{};if($webhook.url){$standalone=$false};Save-Private $configFile $token
            [IO.File]::Delete($pendingFile)
        }finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr);$secret.Dispose();$token=$null}
        $receiver=if($standalone){'standalone'}else{'integrated'};Save-Bytes $receiverFile $utf8.GetBytes($receiver)
        $linkPath=Join-Path ([Environment]::GetFolderPath('Startup')) 'OSTUM subscription verifier.lnk'
        if($standalone -and ($installStartup -or $Mode -eq 'Resume')){
            $shell=(Get-Command powershell.exe).Source;$argsRun='-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Mode Run'
            if($installStartup){$shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut($linkPath);$shortcut.TargetPath=$shell;$shortcut.Arguments=$argsRun;$shortcut.WindowStyle=7;$shortcut.Save()}
            Start-Process -FilePath $shell -ArgumentList $argsRun -WindowStyle Hidden
        }elseif(-not $standalone -and [IO.File]::Exists($linkPath)){[IO.File]::Delete($linkPath)}
        if([IO.File]::Exists($pendingChoice)){[IO.File]::Delete($pendingChoice)}
        $message=if($standalone -and $installStartup){'Telegram подтвердил права бота. Обработчик OSTUM запущен и добавлен в автозапуск Windows.'}elseif($standalone -and $Mode -eq 'Resume'){'Telegram подтвердил права бота. Обработчик запущен в текущем сеансе Windows.'}elseif($standalone){'Права подтверждены. Запустите Issuer.ps1 Run.'}else{'Telegram подтвердил права бота. Подключите режим HandleUpdate к существующему сервису бота. Порядок описан в subscription/README.md.'}
        if($Graphical){[void][Windows.Forms.MessageBox]::Show($message,'OSTUM')}else{Write-Output $message};return
    }
    $stored=$configFile
    if($Mode -eq 'Check' -and [IO.File]::Exists($pendingFile)){$stored=$pendingFile}
    if(-not [IO.File]::Exists($stored)){throw 'Сначала выполните Issuer.ps1 Configure.'}
    do{try{Connect-Channel (Read-Private $stored);break}catch{if($Mode -ne 'Run'){throw};Start-Sleep -Seconds 12}}while($true)
    if($Mode -eq 'Check'){Write-Output ('Telegram подтвердил права @'+$script:username+' в '+$channel);return}
    if($Mode -eq 'HandleUpdate'){
        $update=[Console]::In.ReadToEnd()|ConvertFrom-Json
        $text=if($update.callback_query){$update.callback_query.data}else{$update.message.text}
        if($text -cmatch '^(?:/start\s+)?p_[A-Za-z0-9_-]{24}$'){$script:http=[Net.Http.HttpClient]::new();try{Handle-Update $update}finally{$script:http.Dispose()}};return
    }
    if(-not [IO.File]::Exists($receiverFile) -or [IO.File]::ReadAllText($receiverFile) -ne 'standalone'){throw 'Выбран действующий сервис бота. Передавайте его запросы в режим HandleUpdate.'}
    $webhook=Telegram 'getWebhookInfo' @{};if($webhook.url){throw 'У бота уже настроен webhook. Используйте отдельного бота для OSTUM.'}
    $mutex=[Threading.Mutex]::new($false,'Local\OSTUM.SubscriptionVerifier');try{if(-not $mutex.WaitOne(0)){return}}catch [Threading.AbandonedMutexException]{}
    $script:http=[Net.Http.HttpClient]::new();$offset=0L
    if([IO.File]::Exists($offsetFile) -and [IO.File]::GetLastWriteTimeUtc($offsetFile) -gt [DateTime]::UtcNow.AddDays(-6)){[void][long]::TryParse([IO.File]::ReadAllText($offsetFile),[ref]$offset)}
    Write-Output ('Проверка подписок работает через @'+$script:username+'. Для остановки нажмите Ctrl+C.')
    Service-Event 'started'
    try {while([IO.File]::ReadAllText($receiverFile) -eq 'standalone'){
        try {
            $updates=Telegram 'getUpdates' @{offset=$offset;timeout=30;allowed_updates=@('message','callback_query')}
            Service-Event 'last-poll'
            foreach($update in $updates){
                try{Handle-Update $update}catch{Write-Warning 'Не удалось ответить на запрос. Пользователь может повторить его.'}
                $offset=[long]$update.update_id+1;Save-Bytes $offsetFile $utf8.GetBytes([string]$offset)
            }
        }catch{Write-Warning 'Сервис Telegram недоступен. Повтор через 10 секунд.';Start-Sleep -Seconds 10}
    }}finally{$script:http.Dispose();$mutex.ReleaseMutex();$mutex.Dispose()}
}catch{if($Graphical){Add-Type -AssemblyName System.Windows.Forms;[void][Windows.Forms.MessageBox]::Show($_.Exception.Message,'OSTUM · Подключение бота');exit 1};throw}finally{$script:rsa.Dispose()}
