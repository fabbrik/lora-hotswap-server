# lora-hotswap-server

A .NET host that loads, validates, and hot-swaps LoRA adapters over a shared
base model.

## How this works

**Base model.** A large pretrained language model — here, `Qwen3-0.6B` — made
up of millions of learned numbers (weights). Loading it into memory is slow
and takes gigabytes of space.

**Adapter.** Instead of fine-tuning a whole new copy of the base model to
change its behavior (rewriting all those weights), you can freeze the base
model completely and train a small *extra* set of weights that sits alongside
it and nudges its output. That small extra set is an adapter — typically
megabytes instead of gigabytes, and swappable independently of the base
model it rides on.

**LoRA (Low-Rank Adaptation).** The specific technique used to build these
adapters. Normally, fully fine-tuning a weight matrix `W` means learning a
full-size update matrix of the same shape — expensive to store and train.
LoRA instead approximates that update as the product of two much smaller
("low-rank") matrices, `A` and `B`, so at inference time the model computes
roughly `output = W·x + (A·B)·x`: the frozen base plus a small learned
correction. Only `A` and `B` need to be trained and stored, which is why
LoRA adapters are so much cheaper than full fine-tunes.

**Hot-swap.** Because the base model and its adapters are separate files, the
server can keep the (large, slow-to-load) base model resident in memory and
swap in a different (small, fast-to-load) adapter on demand — changing the
model's effective behavior without restarting the process or reloading the
base weights.

**GGUF.** The binary file format this server's runtime (based on
`llama.cpp`) actually reads at inference time. Adapters are normally
distributed in Hugging Face's `.safetensors` format instead, so before one
can be used here it must be converted to `.gguf` — see step 2 below.

**Quantization (e.g. "Q8_0").** Model weights are usually stored as 16- or
32-bit floating point numbers. Quantization rounds them to lower precision
(here, roughly 8 bits) to shrink file size and speed up inference, at a
small cost in accuracy. "Q8_0" is `llama.cpp`'s name for one specific 8-bit
quantization recipe.

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
`adapters/<name>/adapter.manifest.json` with four fields: the source safetensors
hash (`sourceSha256`), the produced GGUF hash (`ggufSha256`), the converter
revision (`converterRevision`), and `adapterId` -- the stable, content-derived
adapter identity (`sha256:` + the SHA-256 of the source safetensors). Identity
comes from content only: it is invariant across renames and moves and is never a
path, so logs, errors, and responses can name an adapter by it. Each adapter's
outcome (converted / already-current / modified / failed) is reported; the run
exits non-zero iff any adapter is modified or failed. Re-running is safe: with
the gguf + manifest present and all four fields matching disk, it reconverts
nothing and exits 0. Neither the `.gguf` files nor the manifests are committed --
provenance is per-local-conversion.

> **Note:** a *cold* conversion is not offline. It fetches the pinned converter
> from GitHub and each adapter's declared base-model `config.json` from the
> Hugging Face Hub, so a first run needs network access. Once the converter is
> vendored and the adapters are converted, subsequent (warm) runs touch neither.

If an adapter's on-disk `adapter.gguf` or `adapter.manifest.json` no longer
matches its recorded hashes -- or its manifest is missing or disagrees on
`adapterId` (e.g. a pre-1.4 three-field manifest) -- it is reported as `modified`
and left untouched (the tool never overwrites or migrates); the run exits
non-zero. To rebuild that one, delete its `adapter.gguf` and
`adapter.manifest.json` and re-run.

### 3. Build and test the .NET solution

```sh
dotnet build && dotnet test
```

#### Adapter content validation (Story 1.5)

`LoraHotswap.Core` now holds the first domain code: `AdapterConfig` parses an
adapter's `adapter_config.json` (defaulting an absent `use_rslora` to false and
resolving scaling as `alpha/r`, or `alpha/sqrt(r)` under rSLoRA), and
`AdapterValidator` applies the **inert** rule -- an adapter is rejected fatally
as `RejectionReason.Inert` iff it has `lora_B` tensors and every one of them is
exactly zero (`ΔW = B·A = 0`, a silent no-op). Core stays TorchSharp-free:
`AdapterValidator` operates over plain `double` magnitudes.

`LoraHotswap.Infrastructure` owns the native read. Its `SafeTensors.cs` is ported
verbatim from rung 1's `lora-anatomy-dotnet/src/LoraAnatomy/SafeTensors.cs` (the
reuse mandate) behind a `TorchSharp-cpu` reference; `AdapterContentReader` reads
an adapter directory, reduces each `lora_B` tensor to `abs().max()`, and feeds the
Core validator.

The published `ybelkada/opt-350m-lora` ships every `lora_B` exactly zero -- a real
inert adapter -- but it is a gated ~700MB Hub download, so it is **not** committed.
Instead `tests/LoraHotswap.Tests/fixtures/make_inert_fixture.py` (dependency-free,
offline, deterministic) generates a compact seeded reproduction of its defining
all-zero-`lora_B` structure (`inert-ybelkada-opt-350m/`) plus a nonzero-`lora_B`
`trained-control/`. Those committed fixtures back the offline regression test.

See `tools/acquire/acquire.py` for the acquisition tool's own docstring, and
`GOTCHAS.md` for issues hit along the way.
