# Third-party notices

## Magic Pointer (inspiration)
MouseIA was inspired by the ideas and flow of **Magic Pointer**, a Windhawk mod by Rono
(https://github.com/rono-zeroseven/magic-pointer), released under the MIT License.
MouseIA is an independent implementation and does not include Magic Pointer's code. The Google Lens
hand-off follows the same approach (a local page that posts the picture to Lens' upload form).

## ZXing.Net
QR code and barcode reading uses **ZXing.Net** (https://github.com/micjahn/ZXing.Net),
licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0).

## .NET
Built on .NET (https://github.com/dotnet), licensed under the MIT License.

## Components downloaded on first use
These are not shipped inside MouseIA; MouseIA downloads them from their official releases when needed.

- **sherpa-onnx** v1.13.8 (https://github.com/k2-fsa/sherpa-onnx), Apache License 2.0 — offline speech engine for the natural voices.
- **Kokoro-82M** multi-language v1.0 (https://huggingface.co/hexgrad/Kokoro-82M), Apache License 2.0 — the female and male natural voices.
- **espeak-ng** data (https://github.com/espeak-ng/espeak-ng), GPL-3.0 — pronunciation data used by the voice engine, included in the sherpa-onnx voice package.
- **ONNX Runtime** (https://github.com/microsoft/onnxruntime), MIT License — included in the sherpa-onnx package.
- **llama.cpp** (https://github.com/ggml-org/llama.cpp), MIT License — local engine for `.gguf` models.
- **LiteRT-LM** (https://github.com/google-ai-edge/LiteRT-LM), Apache License 2.0 — local engine for `.litertlm` models, installed with **uv** (https://github.com/astral-sh/uv, MIT/Apache-2.0).
