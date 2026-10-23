# Third-party software and assets

Nothing below is redistributed in this repository. It is fetched or built by the tools in `tools/`.

| Component | Used for | Source | License |
|---|---|---|---|
| llama.cpp (pinned in `tools/convert/converter.lock.json`) | `convert_lora_to_gguf.py` vendored at build time into the gitignored `tools/convert/vendor/` | https://github.com/ggml-org/llama.cpp | MIT |
| LLamaSharp | .NET bindings to llama.cpp, the inference runtime | https://github.com/SciSharp/LLamaSharp | MIT |
| `ggml-org/Qwen3-0.6B-GGUF` (Q8_0) | base model | https://huggingface.co/ggml-org/Qwen3-0.6B-GGUF | Apache-2.0 (declared on the Hub, checked 2026-10-22) |
| `phh/Qwen3-0.6B-TLDR-Lora` | demo adapter | https://huggingface.co/phh/Qwen3-0.6B-TLDR-Lora | Apache-2.0 (declared on the Hub) |
| `charent/self_cognition_Alice`, `charent/self_cognition_Bob` | demo adapters | Hugging Face Hub | **No license declared** on the Hub |
| `lucylq/qwen3_06B_lora_math` | demo adapter | Hugging Face Hub | **No license declared** on the Hub |

Model and adapter licenses are set by their authors, not by this repository's MIT license.

The adapters with no declared license are downloaded by the user at their own invocation of
`tools/acquire` and are never committed here. The captured answers quoted in the README and
`docs/three-tenants.md` are short model outputs shown as demonstration. If you fork this to
ship adapters, replace the unlicensed ones with adapters whose license permits it.
