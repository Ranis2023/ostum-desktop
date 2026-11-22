# OSTUM Desktop version history

Use the latest release for a new installation. Each entry below describes the behaviour of that particular version. Versions 5.5 and 5.6 shipped to GitHub users with the 5.7 release. [Download and checksums](https://github.com/Ranis2023/ostum-desktop/releases/latest) · [English user guide](../../README.en.md) · [Документация на русском](../../README.md)

## 5.7.0

Search local conversation history with **Ctrl+Shift+F**, including archived chats if desired. Save the sheet names and key columns from an Excel comparison and reuse them with a new pair of workbooks. Review meeting decisions alongside timestamped excerpts, correct speaker names and export selected items. The storage window shows space used by each component and lets you remove eligible caches after review. [Full English release notes](5.7.0.md#english-release-notes)

## 5.6.0

The sidebar indexes conversation titles, dates and archive status on the PC. Opening a conversation loads its message text. A local Office reader can handle another request for the same workspace and exits after 45 idle seconds. OSTUM checks folder access on every request. When you scroll up in a streaming reply, the view stays on the passage you are reading.

The interface gained 55 vector icons. The microphone changes into sound bars during recording. Click the bars to stop, or cancel during recognition. The calculator can extract a fee already included in a total. A total of 12,480 with an included 20% fee contains a fee of 2,080. [Original notes](5.6.0.md)

## 5.5.0

Windows OCR reads scanned PDF pages locally. Select a recognized line to find its location on the page. Pro users can review a folder of invoices, compare extracted fields with the originals and exclude duplicates before saving. Excel range calculations distinguish blank cells, text and numeric zero. A request to edit multiple cells presents each proposed edit for review.

Open tasks can be filtered by deadline and exported to an ICS calendar file. Questions about two attachments can return source quotations and locations. Image options include 384, 512 and 1024 pixel sizes. The local adapter studio shows base and candidate scores and flags a small evaluation set. [Original notes](5.5.0.md)

## 5.4.0

Choose a working folder and access level from chat. Word and Excel open alongside the conversation in a resizable pane. Review the original and proposed cell value or ask Zarum to rewrite a paragraph in a folder approved for local analysis. Comparing two attachments shows added, removed and moved text.

The optional 2.71 GB ACE-Step component generates 10–60 seconds of instrumental audio locally and exports stereo WAV. Agent indicators show concurrent work. Model settings compare six catalog options. The default Zarum 9B weights and vision projector together occupy 6.60 GB. [Original notes](5.4.0.md)

## 5.3.0

OSTUM can check GitHub at launch and every six hours, announce a new version and download the installer in the background. It verifies the size, SHA-256 and executable version before offering to restart. A managed installation disables online checks. A user on 5.2 or earlier can run the newer installer manually.

Document lookup indexes up to four recently attached documents in memory. On the maintainer's test with eight queries against 240 catalog sheets, the retrieved excerpt contained the target value in all eight cases, up from zero. Median repeat lookup fell from 17.58 ms to 0.10 ms. The initial index build took 56 ms compared with 34 ms previously. These measurements cover retrieval on that test PC. Interrupted model downloads can refresh the link and resume. [Original notes and test inputs](5.3.0.md)

## 5.2.0

The first verified Telegram channel membership unlocks Pro permanently for the installation. On a disconnected workstation, display the QR or transfer the code manually. Open the bot on a connected phone, then bring the signed activation file back by USB. A valid 5.1 file from the same installation is accepted as proof of the earlier check.

The home screen supports file drop, agent activity appears during a task and **F1** opens searchable offline help in Russian. Pro can link an assignment to a Word paragraph or spreadsheet cell, and compare two workbooks by a key column. [Original notes](5.2.0.md) · [English activation and deployment guide](../Company.en.md#activate-pro-and-open-a-project)

## 5.1.0

The workspace gained drag and drop, an example document and a review tab for proposed edits. This release introduced a seven-day Pro activation file issued after Telegram channel membership verification. Version 5.2 introduced permanent activation. Basic chat, voice, photo analysis and drawing were available independently of membership. [Original notes](5.1.0.md)

## 5.0.0

The installer added Office workspaces, local output-layer adapter training on reviewed examples and GGUF import with a trial prompt. Administrators can restrict folder permissions and external requests. A full first-time setup downloads 13.38 GB of model files. The offline kit can be prepared on another computer. [Original notes](5.0.0.md)

## 4.0.0

OSTUM adopted Zarum 9B, based on Qwen3.5-9B, for chat, documents and photos. The orchestrator can run an analyst and a checker in parallel. This version offered a Windows microphone selector, Whisper dictation, spoken replies, optional drawing, and chat archiving and deletion. Existing Sfera preferences and conversations carried over on upgrade. Its installer and download sizes differ from 5.x. [Original release](https://github.com/Ranis2023/ostum-desktop/releases/tag/v4.0.0)
