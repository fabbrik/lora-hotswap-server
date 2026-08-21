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

### 2. Convert the adapters to GGUF

The runtime loads GGUF adapter containers, but acquisition only fetches PEFT
safetensors. Convert them (after step 1) with:

```sh
cd tools/convert && uv run convert.py
```

For each adapter this obtains a pinned copy of llama.cpp's
`convert_lora_to_gguf.py` (recorded in `tools/convert/converter.lock.json`,
vendored under `tools/convert/vendor/` and never committed), produces
`adapters/<name>/adapter.gguf`, and writes a sidecar
`adapters/<name>/adapter.manifest.json` binding the source safetensors hash, the
produced GGUF hash, and the converter revision. Each adapter's outcome
(converted / already-current / modified / failed) is reported; the run exits
non-zero iff any adapter is modified or failed. Re-running is safe: with the gguf
+ manifest present and their hashes matching, it reconverts nothing and exits 0.
Neither the `.gguf` files nor the manifests are committed -- provenance is
per-local-conversion.

> **Note:** a *cold* conversion is not offline. It fetches the pinned converter
> from GitHub and each adapter's declared base-model `config.json` from the
> Hugging Face Hub, so a first run needs network access. Once the converter is
> vendored and the adapters are converted, subsequent (warm) runs touch neither.

If an adapter's on-disk `adapter.gguf` or `adapter.manifest.json` no longer
matches its recorded hashes, it is reported as `modified` and left untouched
(the tool never overwrites); the run exits non-zero. To rebuild that one, delete
its `adapter.gguf` and `adapter.manifest.json` and re-run.

### 3. Build and test the .NET solution

```sh
dotnet build && dotnet test
```

See `tools/acquire/acquire.py` for the acquisition tool's own docstring, and
`GOTCHAS.md` for issues hit along the way.
