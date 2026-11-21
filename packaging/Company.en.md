# Deploy OSTUM Desktop 5.7 on company workstations

[Русская версия](Company.md)

## Prepare an isolated workstation

1. On a connected preparation machine, assemble the offline USB folder or ISO. The full 5.7 kit with dictation, drawing, music and CUDA uses about 17.6 GB. Compare its checksums with a copy obtained through an organization-approved channel. The installer is unsigned. [Kit contents and commands](../README.en.md#prepare-the-offline-usb-folder)
2. Transfer the complete folder to the target machine, or mount the ISO in File Explorer. Run the bundled `Ostum-Desktop-Setup.exe`. For unattended installation under the current Windows account, use `--quiet --offline --no-launch`. The local model, dictation, drawing, Office editors and training use files from the kit.
3. Create `%ProgramData%\OSTUM`, copy the example there as `policy.json` and set the actual project paths and Windows groups. Give ordinary users read access to the policy and appropriate permissions on their project folders. Reserve write access to the policy and its parent directory for administrators. [Example policy](company-policy.example.json)
4. Apply the organization's controls for outbound connections, removable media, backups, encryption and workstation access. Other processes and cloud sync clients can read files independently of OSTUM. For a strictly isolated deployment, choose a local folder outside synchronized directories.

## Activate Pro and open a project

Open **Рабочие пространства Pro**. Scan the QR with a connected phone, or transfer the code or saved QR image on approved media. Start **@rangoostum_bot**. The first verification requires membership in **@ranisgaliaskarov**. Bring the signed `.ostumpro` file back to the workstation and import it into OSTUM. The app verifies the signature locally. Access is permanent for that installation and Windows user. Managed mode shows the QR and code and hides external-link buttons. Use the organization's approved transfer process.

Assignments go into **Поручения OSTUM.xlsx** beside the source. That register contains excerpts and inherits the working folder's access controls. A comparison of two workbooks creates a separate report with source values. Both originals and the report must fall under the same set of project rules. Press **F1** for searchable offline help in Russian.

A saved comparison workflow remembers the sheet names and key columns. Staff review the differences for each new pair of files before saving. Meeting transcription requires local analysis permission in the recording's folder. Users can replay a quote by timestamp, correct speaker labels and select the rows to export. Saving the minutes checks the destination folder's permissions.

History search respects `ChatHistory`. Messages about files in a chat workspace stay in memory during that session. In managed mode the storage window shows component sizes and free space in a read-only view.

## Policy settings

| Setting | Effect |
| --- | --- |
| `Managed` | Enables company mode and disables the app's external network functions, including web search, model downloads, release checks and the link to Telegram. |
| `LocalAI` | Enables local inference. Set it to false when users need only the document editors. |
| `Training` | Enables training globally. The project must also grant training permission. |
| `Projects`, `Groups` | Set absolute project paths and local or domain Windows group names. An empty group list admits users with access to that folder. Nested projects must satisfy every enclosing rule. |
| `Analysis` | Allows the question and an explicitly selected passage to go to local Zarum. The operation is logged. |
| `InternalShares` | Allows UNC paths inside approved roots. Windows checks file-server access. Set to false on a fully disconnected workstation. |
| `ImportedModels`, `ModelFolders` | Allow GGUF imports from approved directories. Users can have read-only access to a verified model library. |
| `DocumentAnalysis` | With `Analysis` enabled, allows excerpts from the open Word document or current Excel sheet to be selected for a chat answer. If the setting is omitted, users select a passage manually. |
| `ChatHistory` | Permits storing ordinary OSTUM conversations. File-workspace messages stay in memory for the session even when ordinary history is enabled. |

## Where data is stored

With history enabled, managed chats and generated images are stored in `%LocalAppData%\SferaAI\pro\chats` and `%LocalAppData%\SferaAI\pro\images`. Personal conversations and the work profile have separate locations. A managed chat uses its own conversation history and excludes the user's personal profile.

Documents stay in the working folder. Previous revisions sit beside them in `.ostum-versions`. Project adapters, held-out evaluation reports and the active version marker live under `.ostum-training\<Windows user SID>` inside the project. Inherited NTFS permissions govern access through Windows. For confidential training, use a dedicated folder with separate NTFS permissions. Revoking an adapter stops its selection for future requests. Apply the organization's retention and backup policy to the examples, reports, versions and originals.

The per-user log at `%LocalAppData%\SferaAI\pro\operations.log` records UTC time, the Windows SID, operation name and a SHA-256 hash of the resource identifier. Document passages and model answers are excluded from this log. Use the organization's infrastructure for centralized audit or tamper-evident retention.

## Check the deployment

On a test account, disconnect the external network route. Edit one paragraph in an editor-only project, save it and restore the previous version. Verify that the same account cannot analyze or train there. In a project that permits analysis, select a paragraph and request a summary. In a training project, use synthetic examples, review the held-out answers, enable the new version, revoke it and repeat the request. Review outbound connection logs using company tools and check project access from a second Windows account. Assess sector-specific deployment, personal data handling and decision-making requirements during rollout.
