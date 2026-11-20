# OSTUM Desktop — offline AI assistant for Windows

[Русская версия](README.md) · [Download for Windows x64](https://github.com/Ranis2023/ostum-desktop/releases/latest/download/Ostum-Desktop-Setup.exe) · [Releases and SHA-256 checksums](https://github.com/Ranis2023/ostum-desktop/releases)

OSTUM runs **Zarum 9B**, a local model based on Qwen3.5-9B, on your own PC. Chat, photo analysis, document reading, dictation and image generation keep working when the network goes down. The interface and built-in help are in Russian.

![OSTUM Desktop 5.2 home screen with entry points for chat, files and images](assets/home-5.2.png)

## Install on Windows 10 or 11

Download and run **Ostum-Desktop-Setup.exe**. On a new PC, the standard installation downloads six model files totalling **13.38 GB** and verifies their SHA-256 hashes. It installs the local engines and adds CUDA acceleration on supported NVIDIA systems. Allow **at least 20 GB of free SSD space** for a full installation. The current release is unsigned. The checksum is published with the installer.

For an air-gapped PC, prepare the full **offline ISO or USB folder** on a connected machine, bring it to the target PC and run the installer from that media. GitHub Releases hosts the network installer. The weights come from the model sources during setup, or from the prepared offline media. [Offline build instructions](README.md#полная-установка-без-интернета) · [Model catalogue](src/catalog.json)

## Release notifications

Starting with 5.3, OSTUM checks GitHub after launch and every six hours. A button in the main window shows the new version. When minimized, OSTUM also sends one Windows notification per release. The app can download the installer in the background and check its published SHA-256 before you click to install and restart. Chats, Pro access and the model library are retained. Click the OSTUM logo to change either preference. Managed installations keep external requests disabled. For 5.2 and earlier, install 5.3 once using the Windows download above.

## What you can do

| Task | How it works |
| --- | --- |
| Talk to a local assistant | Chat with streaming answers, dictate using a selected Windows microphone and listen to local speech output. |
| Work with images and documents | Attach a photograph, a screenshot, a text PDF or a DOCX. Generate images locally with Z-Image-Turbo after the drawing component is installed. |
| Use multiple agents | For a team request, the analyst and fact checker work in parallel. The chat shows each agent's activity. Web search is optional and off by default. |
| Work on company files in Pro | Open DOCX or XLSX inside the app, review suggested edits and keep versions. Compare two spreadsheets by a key column or create a task linked to the source paragraph or cell. |
| Adapt the model locally | Train a project-specific output-layer LoRA on reviewed question-and-answer examples. Compare the base model and the candidate on held-out examples before enabling it. |
| Bring another local model | Import a compatible GGUF in model settings. Image understanding requires a matching vision projector and a supported architecture. Run the built-in benchmark to measure performance on your PC. |

**Pro access.** The first verified subscription to the developer's Telegram channel unlocks Pro permanently. To activate an isolated workstation, scan its QR code with a connected phone, start the bot and transfer the signed `.ostumpro` response by USB. The response is bound to the Windows user and workstation. [Developer's channel](https://t.me/ranisgaliaskarov) · [Activation bot](https://t.me/rangoostum_bot) · [Company deployment guide in Russian](packaging/Company.md)

## Hardware and performance

For Zarum 9B, 16 GB of system RAM is a practical starting point. A Ryzen 7 7435HS with 16 GB of RAM and an RTX 4050 Laptop GPU with 6 GB of VRAM measured **13.1 tokens/s** on the short built-in CUDA benchmark. The same CPU measured **6.8 tokens/s** on a separate CPU-only run. Model size, context length, available memory and GPU load affect results. [Test conditions and larger configurations](README.md#какой-компьютер-подойдёт)

The desktop app is written in C# and WPF for .NET Framework 4.8. OSTUM code is MIT-licensed. Base models, runtimes and third-party components have their own licences. [MIT licence](LICENSE) · [Component sources](packaging/Sources.txt) · [Russian build guide](README.md#сборка-и-развитие)
