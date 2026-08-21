# lora-hotswap-server

A .NET host that loads, validates, and hot-swaps LoRA adapters over a shared
base model. See `docs/` in the parent workspace for the epic and story specs
driving this build.

## Getting started

### 1. Acquire the base model and adapters

Nothing in this repo ships model weights or adapter binaries. A fresh clone
downloads them with:

```sh
cd tools/acquire && uv run acquire.py
```

This pulls the pinned base model (`ggml-org/Qwen3-0.6B-GGUF`, Q8_0 quant)
into `models/` and the four LoRA adapters into `adapters/`, then records each
artifact's resolved Hub revision and per-file sha256 to the committed
`tools/acquire/acquired.lock.json`. Re-running it is safe: with everything
already on disk and matching the lock it downloads nothing and exits 0.

### 2. Build and test the .NET solution

```sh
dotnet build && dotnet test
```

See `tools/acquire/acquire.py` for the acquisition tool's own docstring, and
`GOTCHAS.md` for issues hit along the way.
