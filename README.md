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

#### Adapter admission (Story 1.6)

Startup now *admits* adapters before the host will serve. Admission runs after
the web host is built and **before any listener binds**: on any failure the
process writes RFC 9457 reasons to stderr and exits non-zero, so a
misconfigured deployment never opens a socket. On success the immutable
`AdapterRegistry` is registered as a singleton for later stories.

Each adapter is judged by the pure `Admission.Admit` decision (in
`LoraHotswap.Core`) over artifacts read through the `IAdapterArtifactsReader`
port. The checks apply in a deliberate, **first-problem-wins** order:

1. **Missing manifest** — an adapter with no `adapter.manifest.json` is rejected.
   Provenance is *never* inferred from filenames, timestamps, directory names, or
   GGUF metadata — only from the manifest's recorded hashes.
2. **Provenance hashes** — the SHA-256 of `adapter_model.safetensors` and of
   `adapter.gguf` are recomputed from raw file bytes (a streaming hash, the .NET
   analogue of `convert.py`'s `sha256_of`) and compared to the manifest's
   `sourceSha256` / `ggufSha256`. A mismatch is rejected and the reason **names
   which hash diverged** (source is checked before gguf). The admitted
   `AdapterId` is derived from the *recomputed* source hash
   (`sha256:` + that hash), never trusted from the manifest's self-declared
   `adapterId` field — identity is authoritative and content-derived.
3. **Base model** — the adapter's `base_model_name_or_path` is compared to the
   configured base model by **strict ordinal string equality**. No normalization,
   case-folding, or vendor-prefix stripping. The real `qwen3_06B_lora_math`
   declares `unsloth/Qwen3-0.6B` for weights compatible with `Qwen/Qwen3-0.6B`;
   its rejection is asserted as **correct-by-design**, not a bug to normalize away.
4. **Inert** — Story 1.5's rule, composed (not re-implemented): an adapter whose
   `lora_B` tensors are all zero is rejected as its own distinct problem.

Every failure mode carries a **distinct RFC 9457 `type` URI** under
`https://lora-hotswap/problems/` (`missing-manifest`, `hash-mismatch`,
`base-model-mismatch`, `inert-adapter`, `unknown-adapter`), so later HTTP
surfacing (Story 2.1) and log correlation stay unambiguous.

Every configured **tenant** must resolve to an admitted `AdapterId`; a tenant
referencing an unadmitted adapter fails startup with an `unknown-adapter`
problem. The base-model name, adapters root, adapter list, and the
tenant → `AdapterId` map are declared under the `Admission` section of
`appsettings.json`, bound to `AdmissionOptions`, and validated at startup.

The `AdapterRegistry` is **immutable**: constructed once from admitted entries,
it exposes lookup only and has no public add/remove/replace/evict member. Adding
an adapter requires a restart (a reflection test proves the type is mutator-free).

Admission is covered offline by committed fixtures under
`tests/LoraHotswap.Tests/fixtures/admission/` (`valid/`, `tampered-source/`,
`tampered-gguf/`, `no-manifest/`, `base-mismatch/`, `inert/`), regenerated
deterministically and dependency-free by `make_admission_fixtures.py` — each
directory carries a tiny `.gguf` stand-in and a manifest whose hashes are computed
to match (or deliberately not).

#### Merge verification (Story 1.7)

`MergeVerifier` (Infrastructure) checks an adapter's merge **two-sidedly**, module
by module: the computed `W' = W0 + scaling · (B @ A)` must match the Python PEFT
merged weight within `1e-5` **and** differ from `W0` by more than that tolerance.
Either side alone never passes: reference equality proves the arithmetic, and
difference from base proves the adapter does something. The merge is rung 1's
arithmetic (`MergeArithmetic`), the tensors are read by the ported `SafeTensors.cs`,
and scaling comes from `AdapterConfig.Scaling`. Shapes are checked explicitly so
broadcasting can't hide a transposed or wrong-rank delta.

The references are real PEFT output. `tests/LoraHotswap.Tests/fixtures/make_merge_fixtures.py`
follows rung 1's PEFT recipe (build base, `get_peft_model`, `merge_and_unload`) and runs
in rung 1's locked environment. It writes `fixtures/merge/scaling-4/`
(alpha/r = 4) and `fixtures/merge/scaling-1/` (alpha/r = 1), each holding the adapter
plus `merge_reference.safetensors` (`<module>.w0` / `<module>.w_merged`):

```sh
cd tests/LoraHotswap.Tests/fixtures
uv run --project ../../../../lora-anatomy-dotnet/reference python make_merge_fixtures.py
```

**Scaling 1 cannot catch a missing scaling factor on its own**: `W0 + B@A` equals
`W0 + 1·B@A`. The suite shows the same wrong implementation passing on scaling 1
and failing on scaling 4.

#### Admission tracing (Story 1.8)

The system has one `ActivitySource`, `LoraHotswap`. Admission emits one
`adapter.admission` span per adapter, carrying `adapter.id`, `adapter.name` (a
label), `admission.result` (`admitted` / `rejected` / `error`) and
`admission.reason`. A rejected adapter's `adapter.id` is the content hash of its
source bytes; an adapter with no source bytes is tagged `unidentified`, never a
path. Spans go to the console by default, with no collector needed. Set
`OpenTelemetry:OtlpEndpoint` (or `OTEL_EXPORTER_OTLP_ENDPOINT`) to also export
over OTLP. `OpenTelemetry:OtlpProtocol` (`grpc` or `http/protobuf`) overrides the
protocol; when it is empty, `OTEL_EXPORTER_OTLP_PROTOCOL` applies, then `grpc`. With
`http/protobuf`, a base endpoint such as `http://collector:4318` has `/v1/traces`
appended. Only `LoraHotswap` spans are exported, so every exported span carries
`adapter.id`. An invalid protocol or endpoint fails startup with a reason line.

#### Running-instance verification (Story 1.9)

```sh
curl -X POST http://localhost:5000/v1/adapters/sha256:<hash>/verify
```

This runs the Story 1.7 verification for an admitted adapter against the
`merge_reference.safetensors` next to its source safetensors. It first re-hashes
the source file against the admitted identity, so an adapter changed on disk
since admission is reported as drift, never verified under the old identity. It
uses the admitted config's scaling and checks every module the reference covers.
It returns `match`, `mismatch` or `inert`, with per-module measurements, and emits
an `adapter.verification` span carrying `adapter.id` and `verification.outcome`
(`unidentified` for a malformed ID). Failures return typed problems:

| Case | Status | Problem type |
| --- | --- | --- |
| Malformed adapter ID (not `sha256:` + 64 lowercase hex) | 400 | `invalid-request` |
| Adapter was never admitted | 404 | `unknown-adapter` |
| Adapter's source changed since admission | 409 | `hash-mismatch` |
| Adapter has no reference | 422 | `merge-reference-missing` |
| Unexpected fault (e.g. an unreadable reference) | 500 | `internal-error` |

The real adapters' references are generated, not committed (they are 75–117 MB
each and live in the gitignored `adapters/`). Generate them once after
acquisition:

```sh
uv run --project ../lora-anatomy-dotnet/reference python tools/reference/make_references.py
```

The tool merges each adapter with PEFT's `merge_and_unload` and writes `w0` /
`w_merged` for 6 targeted modules spread across the layers (`N_TARGETS`). The base
is `unsloth/Qwen3-0.6B`, rung 1's cached mirror of Qwen3-0.6B (`BASE_ID` overrides
it). All four acquired adapters verify as `match` with a reference error of 0.

No weights are read from a live inference context (AD-5).

#### Tenant serving (Stories 2.1–2.4)

```sh
curl -X POST http://localhost:5000/v1/generate -H 'content-type: application/json' \
     -d '{"tenant":"alice","prompt":"hello"}'
```

The response names the tenant, the adapter the runtime **reported applied**, the
full applied set, and the active binding strategy. Phase A serves through
`DeterministicInferenceRuntime` (AD-8), which records the exact adapter set
applied to each context and returns placeholder text.

The binding strategy is configured under `Serving`:

- **`PerRequestApply`** (default): `ContextPoolSize` generic contexts (default 4).
  The tenant's adapter is applied on every lease.
- **`PerTenantResident`**: one context per tenant, with its adapter applied once
  at startup. A failure there exits startup non-zero.

A request waits up to `LeaseTimeoutMilliseconds` (default 2000) for a free
context. A context is never shared between concurrent requests.

Every rejection is fail-closed and typed, under one `https://lora-hotswap/problems/`
prefix:

| Problem type | Status | Notes |
| --- | --- | --- |
| `invalid-request` | 400 | Missing or blank fields, a malformed JSON body, or a non-JSON content type |
| `unknown-tenant` | 404 | |
| `lease-unavailable` | 503 | Sent with `Retry-After` |
| `adapter-not-applied` | 500 | Also returned when the runtime reports an empty or foreign applied set |
| `generation-failed` | 500 | |
| `base-model-mismatch` | 500 | |
| `internal-error` | 500 | An unexpected fault; the detail is generic |

`inert-adapter` is rejected only at admission, and fails startup: it never reaches
a request. `base-model-mismatch` is also rejected at admission; the serve path
re-checks it. Warning and error logs name the tenant and its adapter. No
code path returns output without the tenant's adapter reported applied, and
problem details never carry internal exception text.

See `tools/acquire/acquire.py` for the acquisition tool's own docstring, and
`GOTCHAS.md` for issues hit along the way.
