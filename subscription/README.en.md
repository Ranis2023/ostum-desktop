# OSTUM Pro activation

To activate a workstation, open **Рабочие пространства Pro** and scan its QR code with a connected phone. Start the official bot, verify your subscription to the developer's channel and copy the signed `.ostumpro` file back to the workstation. Drag it into OSTUM. Since 5.2 the first successful check grants permanent access for that installation and Windows user. You can transfer the activation code manually when no camera is available. [Official bot](https://t.me/rangoostum_bot) · [Developer's channel](https://t.me/ranisgaliaskarov) · [English deployment guide](../packaging/Company.en.md) · [Русская версия](README.md)

## Run the issuer

1. The publisher adds the dedicated bot as an administrator of the channel and obtains its API token from BotFather. Telegram requires administrator rights to check another user's membership reliably. [Telegram Bot API](https://core.telegram.org/bots/api#getchatmember)
2. In the Windows account that owns the issuer key, run `Configure.cmd` in this directory. Paste the token into the masked field. For a dedicated bot, choose the local handler and enable startup at sign-in if desired. Saving checks the configuration with Telegram. The token and private key are protected with Windows DPAPI outside the repository.
3. Run the following from the repository root in Windows PowerShell. `Check` verifies bot identity, channel and administrator rights. `Run` polls for updates and checks for an existing webhook first. `Resume` repeats the checks using the stored token after an interrupted setup. A connected issuer is needed to issue new files. An activated workstation verifies its file locally.

```powershell
.\subscription\Issuer.ps1 -Mode Check
.\subscription\Issuer.ps1 -Mode Run
```

`%LocalAppData%\OSTUM-Subscription-Issuer` holds the timestamps of the last successful poll and file delivery. To integrate a dedicated bot with an existing handler, configure integration mode. In a trusted issuer environment, pass an update verified by that handler on standard input to `Issuer.ps1 -Mode HandleUpdate`. Only the trusted handler should have access to this input.

## Offline verification and backups

The file carries a random installation identifier, issue time, access type and an RSA-SHA256 signature. OSTUM checks the signature with its embedded public key and matches the installation. Telegram receives the ordinary bot interaction. Workstation documents, paths, conversations, phone number and username are excluded from the activation file.

After the first successful check, the issuer stores a protected record under an opaque name in its `subscribers` directory. The same Telegram account can later obtain a file for a new installation code. Back up that directory, the publisher's private key and the Windows account's DPAPI material together. Version 5.1 kept no subscriber registry.

Version 5.2 requests an OP2 permanent file with the `p2_` prefix. The issuer also accepts legacy `p_` requests and issues OP1 for 5.1. Version 5.2 accepts a genuine OP1 for the same installation as proof of an earlier check. Only the public key ships in the USB kit.

A fork operating its own bot needs its own bot and channel identifiers and key pair. For a new project, `Issuer.ps1 -Mode Initialize` generates the pair and stops if an existing public key conflicts with the issuer store. The public half is `issuer-public.xml`.
