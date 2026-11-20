$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'subscription/Issuer.ps1')
function ConvertTo-SecureString {throw 'The optional Windows PowerShell Security module is unavailable'}
$fixtureFile=[IO.Path]::Combine([IO.Path]::GetTempPath(),('ostum-test-'+[Guid]::NewGuid().ToString('N')+'.dpapi'))
$fixtureSecret=New-MaskedSecret 'synthetic-form-test';$ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($fixtureSecret)
try{
    Save-Private $fixtureFile ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr));Save-Private $fixtureFile 'synthetic-second-test'
    if((Read-Private $fixtureFile) -cne 'synthetic-second-test'){throw 'Protected form input not saved'}
    Write-Output 'PASS masked input, atomic replacement and Windows DPAPI with the optional Security cmdlet unavailable'
}finally{[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr);$fixtureSecret.Dispose();if([IO.File]::Exists($fixtureFile)){[IO.File]::Delete($fixtureFile)}}
# Only transport is substituted. The real handler builds the Telegram document and signs it with a disposable test key.
Add-Type -TypeDefinition @'
using System;using System.Net;using System.Net.Http;using System.Threading;using System.Threading.Tasks;
public sealed class TelegramReplyCapture : HttpMessageHandler {
 public string Document;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct){
   var form=(MultipartFormDataContent)req.Content;
   foreach(var part in form)if(part.Headers.ContentDisposition.Name.Trim('"')=="document")Document=await part.ReadAsStringAsync();
   return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"ok\":true}")};
 }
}
'@ -ReferencedAssemblies System.Net.Http
$script:api='https://test.invalid/';$script:channelId=-100123L;$script:user=71L;$script:botId=842L;$script:selfStatus='administrator'
$csp=[Security.Cryptography.CspParameters]::new(24);$csp.Flags='CreateEphemeralKey'
$script:rsa=[Security.Cryptography.RSACryptoServiceProvider]::new(2048,$csp)
$capture=[TelegramReplyCapture]::new();$script:http=[Net.Http.HttpClient]::new($capture)
function Service-Event([string]$Name){if($Name -ne 'last-issue'){throw 'Unexpected test event'}}
$device='AaBbCcDdEeFf0123456789_-';$script:memberId=$script:user;$script:restricted=$false
function Telegram([string]$Method,[hashtable]$Body){
    switch($Method){
        'getChatMember'{if($Body.chat_id -ne $script:channelId){throw 'Wrong channel'};if($Body.user_id -eq $script:botId){return @{user=@{id=$script:botId};status=$script:selfStatus}};if($Body.user_id -ne $script:user){throw 'Wrong membership target'};if($script:status -eq 'error'){throw 'offline'};return @{user=@{id=$script:memberId};status=$script:status;is_member=$script:restricted}}
        'sendMessage'{$script:lastText=$Body;return @{} }
        'answerCallbackQuery'{return @{} }
        default{throw 'Unexpected Bot API call'}
    }
}
function Assert-Bot([bool]$ok,[string]$reason){if(-not $ok){throw $reason}}
$msg=@{from=@{id=$script:user;is_bot=$false};chat=@{type='private';id=$script:user};text=('/start p_'+$device)}
try {
    foreach($kind in @('left','kicked','restricted','unknown','error','member','administrator','creator','restricted-member','wrong-id')){
        $script:restricted=$kind -eq 'restricted-member';$script:status=$kind;$script:memberId=$script:user
        if($script:restricted){$script:status='restricted'};if($kind -eq 'wrong-id'){$script:memberId=72;$script:status='member'}
        $capture.Document=$null;$script:lastText=$null;Handle-Update @{message=$msg}
        $expected=$kind -in @('member','administrator','creator','restricted-member')
        Assert-Bot ([bool]$capture.Document -eq $expected) ('Membership '+$kind)
        if($expected){
            $parts=$capture.Document.Split('.');Assert-Bot ($parts.Length -eq 5 -and $parts[1] -ceq $device -and ([long]$parts[3]-[long]$parts[2]) -eq 604800) 'Receipt fields'
            $base=$parts[4].Replace('-','+').Replace('_','/');$sig=[Convert]::FromBase64String($base.PadRight([int]([Math]::Ceiling($base.Length/4.0)*4),'='))
            Assert-Bot ($script:rsa.VerifyData([Text.Encoding]::ASCII.GetBytes(($parts[0..3] -join '.')),'SHA256',$sig)) 'Receipt signature'
        }elseif($kind -eq 'left'){Assert-Bot ($script:lastText.reply_markup.inline_keyboard[0][1].callback_data -ceq ('p_'+$device)) 'Retry button lost the device'}
    }
    $script:status='member';$script:memberId=$script:user;$capture.Document=$null
    Handle-Update @{callback_query=@{id='query';from=$msg.from;message=$msg;data=('p_'+$device)}};Assert-Bot ([bool]$capture.Document) 'Retry after subscribing'
    $capture.Document=$null;$script:selfStatus='member';Handle-Update @{message=$msg};Assert-Bot (-not $capture.Document) 'Bot lost administrator rights';$script:selfStatus='administrator'
    $capture.Document=$null;$msg.chat.type='group';Handle-Update @{message=$msg};Assert-Bot (-not $capture.Document) 'Group granted'
    $msg.chat.type='private';$msg.from.id=99;Handle-Update @{message=$msg};Assert-Bot (-not $capture.Document) 'Mismatched sender granted'
    Write-Output 'PASS real membership handler, callback retry, sender binding, denied and failed lookups, verified signature and seven-day file delivery through mocked Telegram transport'
}finally{$script:http.Dispose();$script:rsa.Dispose()}
