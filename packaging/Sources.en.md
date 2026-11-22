# Engines, models and licences

OSTUM calls `llama-server` on the loopback address `127.0.0.1` and starts `sd-cli` as a child process. Model downloads use Hugging Face. The application catalogue records file URLs, sizes and SHA-256 digests. [Catalogue](../src/catalog.json) · [Original component inventory in Russian](Sources.txt) · [OSTUM application licence](../LICENSE)

| Component | Source and version used by OSTUM |
| --- | --- |
| Local inference | llama.cpp build b12547, MIT. [Upstream release](https://github.com/ggml-org/llama.cpp/releases/tag/b12547) |
| Image engine | stable-diffusion.cpp build master-1106-532d2c1, MIT. [Upstream release](https://github.com/leejet/stable-diffusion.cpp/releases/tag/master-1106-532d2c1) |
| Zarum 9B | Qwen3.5-9B by the Qwen team, Apache 2.0. OSTUM uses Unsloth's Q4_K_M GGUF and F16 vision projector. OSTUM adapters are separate from the base weights. [Qwen model card](https://huggingface.co/Qwen/Qwen3.5-9B) · [Quantized files](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF) |
| Drawing | Z-Image-Turbo by Tongyi-MAI, with a GGUF from leejet, an Unsloth Qwen3-4B-Instruct-2507 text encoder and a Comfy-Org VAE. [Base model](https://huggingface.co/Tongyi-MAI/Z-Image-Turbo) · [Generator](https://huggingface.co/leejet/Z-Image-Turbo-GGUF) · [Encoder](https://huggingface.co/unsloth/Qwen3-4B-Instruct-2507-GGUF) · [VAE distribution](https://huggingface.co/Comfy-Org/z_image_turbo) |
| Dictation | whisper.cpp b5454 with the multilingual Whisper base Q8_0 model. Windows WASAPI recording uses NAudio 2.2.1. [Engine](https://github.com/ggml-org/whisper.cpp/releases/tag/b5454) · [Speech models](https://huggingface.co/ggerganov/whisper.cpp) · [NAudio](https://github.com/naudio/NAudio/tree/v2.2.1) |
| Local tools | Portable CPython 3.14.10 (PSF licence) and NumPy 2.5.5 (BSD-3-Clause and bundled library licences). Licence files accompany the modules in `OstumTools.zip`. [CPython](https://www.python.org/downloads/release/python-31410/) · [NumPy](https://pypi.org/project/numpy/2.5.5/) |
| Speech output | eSpeak NG 1.52.0, GPL-3.0-or-later, runs in a separate process. `speech/corresponding-source.zip` inside `OstumTools.zip` contains the matching source archive and licence. [Upstream release](https://github.com/espeak-ng/espeak-ng/releases/tag/1.52.0) |
| Optional music | ACE-Step 1.5 uses the native GGML ace-synth runtime and GGUF model files. The first music request downloads any required files from the catalogue. [Packaged runtime release](https://github.com/ace-step/acestep.vst3/releases/tag/v0.1.0) · [Native implementation](https://github.com/ServeurpersoCom/acestep.cpp) · [GGUF weights](https://huggingface.co/Serveurperso/ACE-Step-1.5-GGUF) · [Embedding model](https://huggingface.co/Qwen/Qwen3-Embedding-0.6B) |
