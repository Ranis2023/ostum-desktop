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
        'getChatMember'{$script:lookups++;if($Body.chat_id -ne $script:channelId){throw 'Wrong channel'};if($Body.user_id -eq $script:botId){return @{user=@{id=$script:botId};status=$script:selfStatus}};if($Body.user_id -ne $script:user){throw 'Wrong membership target'};if($script:status -eq 'error'){throw 'offline'};return @{user=@{id=$script:memberId};status=$script:status;is_member=$script:restricted}}
        'sendMessage'{$script:lastText=$Body;return @{} }
        'answerCallbackQuery'{return @{} }
        default{throw 'Unexpected Bot API call'}
    }
}
function Assert-Bot([bool]$ok,[string]$reason){if(-not $ok){throw $reason}}
$privateDir=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('ostum-issuer-qa-'+[guid]::NewGuid().ToString('N')))).FullName
$msg=@{from=@{id=$script:user;is_bot=$false};chat=@{type='private';id=$script:user};text=('/start p2_'+$device)}
try{
    foreach($kind in @('left','kicked','restricted','unknown','error','member','administrator','creator','restricted-member','wrong-id')){
        $script:user++;$msg.chat.id=$msg.from.id=$script:user
        $script:restricted=$kind -eq 'restricted-member';$script:status=$kind;$script:memberId=$script:user
        if($script:restricted){$script:status='restricted'};if($kind -eq 'wrong-id'){$script:memberId=$script:user+500;$script:status='member'}
        $capture.Document=$null;$script:lastText=$null;Handle-Update @{message=$msg}
        $expected=$kind -in @('member','administrator','creator','restricted-member')
        Assert-Bot ([bool]$capture.Document -eq $expected) ('Membership '+$kind)
        if($expected){
            $parts=$capture.Document.Split('.');Assert-Bot ($parts.Length -eq 5 -and $parts[0] -ceq 'OP2' -and $parts[1] -ceq $device -and $parts[3] -ceq 'permanent') 'Permanent receipt fields'
            $base=$parts[4].Replace('-','+').Replace('_','/');$sig=[Convert]::FromBase64String($base.PadRight([int]([Math]::Ceiling($base.Length/4.0)*4),'='))
            Assert-Bot ($script:rsa.VerifyData([Text.Encoding]::ASCII.GetBytes(($parts[0..3] -join '.')),'SHA256',$sig)) 'Signature'
        }elseif($kind -eq 'left'){Assert-Bot ($script:lastText.reply_markup.inline_keyboard[0][1].callback_data -ceq ('p2_'+$device)) 'Retry button lost the request'}
    }
    $script:memberId=$script:user;$script:status='member';$capture.Document=$null;$script:selfStatus='member'
    Handle-Update @{message=$msg};Assert-Bot (-not $capture.Document) 'Bot lost admin before first check';$script:selfStatus='administrator'
    Handle-Update @{callback_query=@{id='qa';from=$msg.from;message=$msg;data=('p2_'+$device)}};Assert-Bot ([bool]$capture.Document) 'Retry'
    $script:status='left';$capture.Document=$null;$readBefore=$script:lookups;$other='0123456789_-AaBbCcDdEeFf'
    $msg.text='/start p2_'+$other;Handle-Update @{message=$msg}
    Assert-Bot ($capture.Document.StartsWith('OP2.'+$other+'.') -and $script:lookups -eq $readBefore) 'Permanent reissue for a second device'
    $msg.text='/start p_'+$device;Handle-Update @{message=$msg};$old=$capture.Document.Split('.')
    Assert-Bot ($old[0] -ceq 'OP1' -and [long]$old[3]-[long]$old[2] -eq 604800) 'Legacy 5.1 compatibility'
    $capture.Document=$null;$msg.chat.type='group';Handle-Update @{message=$msg};Assert-Bot (-not $capture.Document) 'Group granted'
    $msg.chat.type='private';$otherActor=@{id=99;is_bot=$false};Handle-Update @{callback_query=@{id='qa';from=$otherActor;message=$msg;data=('p2_'+$device)}};Assert-Bot (-not $capture.Document) 'Mismatched callback granted'
    $msg.from.id=99;Handle-Update @{message=$msg};Assert-Bot (-not $capture.Document) 'Mismatched sender granted'
    Write-Output 'PASS membership, retry, identity, signed lifetime file, reissue after unsubscribe without lookup, legacy seven-day client'
}finally{$script:http.Dispose();$script:rsa.Dispose()}
