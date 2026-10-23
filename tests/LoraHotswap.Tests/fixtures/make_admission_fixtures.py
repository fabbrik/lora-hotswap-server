#!/usr/bin/env python3
"""Generate compact, offline, deterministic fixtures for admission (Story 1.6).

Dependency-free: standard-library ``struct``, ``json``, ``hashlib`` only -- no
numpy, no torch, no network. Mirrors the real adapter layout: each directory has
an ``adapter_model.safetensors`` (source), an ``adapter.gguf`` (a tiny stand-in
blob -- admission hashes raw file bytes, so its content is arbitrary), and an
``adapter.manifest.json`` whose four fields (``sourceSha256``, ``ggufSha256``,
``converterRevision``, ``adapterId``) mirror ``tools/convert/convert.py`` exactly.

Directories produced (each < 1 MB):

  valid/           matching manifest hashes, base ``Qwen/Qwen3-0.6B``, non-inert.
  tampered-source/ manifest ``sourceSha256`` deliberately wrong; gguf matches.
  tampered-gguf/   manifest ``ggufSha256`` deliberately wrong; source matches.
  no-manifest/     valid content but no ``adapter.manifest.json`` at all.
  base-mismatch/   matching hashes, non-inert, base ``unsloth/Qwen3-0.6B`` (the
                   vendor-prefix trap seen on published adapters).
  inert/           matching hashes, base ``Qwen/Qwen3-0.6B``, every lora_B zero.

Deterministic: re-running rewrites byte-identical files.
"""

import hashlib
import json
import os
import struct

HERE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "admission")

CONFIGURED_BASE = "Qwen/Qwen3-0.6B"
MISMATCH_BASE = "unsloth/Qwen3-0.6B"
CONVERTER_REVISION = "0" * 40  # a plausible 40-hex converter revision

R = 8
IN_FEATURES = 64
OUT_FEATURES = 64

MODULES = [
    "base_model.model.model.layers.0.self_attn.q_proj",
    "base_model.model.model.layers.0.self_attn.v_proj",
]


def _lcg_floats(seed, count):
    state = seed & 0xFFFFFFFF
    out = []
    for _ in range(count):
        state = (1664525 * state + 1013904223) & 0xFFFFFFFF
        out.append((state / 0xFFFFFFFF) * 0.1 - 0.05)
    return out


def _f32_bytes(values):
    return struct.pack("<" + "f" * len(values), *values)


def _safetensors_bytes(tensors):
    header = {}
    blob = bytearray()
    for name, shape, values in tensors:
        start = len(blob)
        blob += _f32_bytes(values)
        header[name] = {
            "dtype": "F32",
            "shape": list(shape),
            "data_offsets": [start, len(blob)],
        }
    header_json = json.dumps(header, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return struct.pack("<Q", len(header_json)) + header_json + bytes(blob)


def _tensors(inert):
    tensors = []
    for i, module in enumerate(MODULES):
        a_vals = _lcg_floats(seed=1000 + i, count=R * IN_FEATURES)
        tensors.append((f"{module}.lora_A.weight", (R, IN_FEATURES), a_vals))
        n_b = OUT_FEATURES * R
        b_vals = [0.0] * n_b if inert else _lcg_floats(seed=5000 + i, count=n_b)
        tensors.append((f"{module}.lora_B.weight", (OUT_FEATURES, R), b_vals))
    return tensors


def _config_bytes(base_model):
    config = {
        "base_model_name_or_path": base_model,
        "bias": "none",
        "lora_alpha": 16,
        "lora_dropout": 0.0,
        "peft_type": "LORA",
        "r": R,
        "target_modules": ["q_proj", "v_proj"],
        "task_type": "CAUSAL_LM",
    }
    return (json.dumps(config, indent=2, sort_keys=True) + "\n").encode("utf-8")


def _gguf_bytes(seed):
    # A tiny deterministic stand-in for the real GGUF container. Admission hashes
    # raw bytes, so only determinism matters, not GGUF validity.
    body = bytes(_int_bytes(seed))
    return b"GGUF" + body


def _int_bytes(seed):
    state = seed & 0xFFFFFFFF
    out = []
    for _ in range(64):
        state = (1664525 * state + 1013904223) & 0xFFFFFFFF
        out.append(state & 0xFF)
    return out


def _sha256(data):
    return hashlib.sha256(data).hexdigest()


def _corrupt_hex(h):
    # Flip the first hex digit to guarantee a mismatch while staying valid hex.
    first = "1" if h[0] == "0" else "0"
    return first + h[1:]


def _write(path, data):
    with open(path, "wb") as f:
        f.write(data)


def _generate(name, base_model, inert, manifest_mode):
    out_dir = os.path.join(HERE, name)
    os.makedirs(out_dir, exist_ok=True)

    source = _safetensors_bytes(_tensors(inert))
    gguf = _gguf_bytes(seed=hash_name(name))
    config = _config_bytes(base_model)

    _write(os.path.join(out_dir, "adapter_model.safetensors"), source)
    _write(os.path.join(out_dir, "adapter.gguf"), gguf)
    _write(os.path.join(out_dir, "adapter_config.json"), config)

    if manifest_mode == "none":
        # Ensure no stale manifest lingers from a prior run.
        stale = os.path.join(out_dir, "adapter.manifest.json")
        if os.path.exists(stale):
            os.remove(stale)
        print(f"wrote {name} (no manifest)")
        return

    source_sha = _sha256(source)
    gguf_sha = _sha256(gguf)
    adapter_id = "sha256:" + source_sha

    if manifest_mode == "tampered-source":
        source_sha = _corrupt_hex(source_sha)
    elif manifest_mode == "tampered-gguf":
        gguf_sha = _corrupt_hex(gguf_sha)

    manifest = {
        "adapterId": adapter_id,
        "converterRevision": CONVERTER_REVISION,
        "ggufSha256": gguf_sha,
        "sourceSha256": source_sha,
    }
    manifest_text = json.dumps(manifest, indent=2, sort_keys=True) + "\n"
    _write(os.path.join(out_dir, "adapter.manifest.json"), manifest_text.encode("utf-8"))
    print(f"wrote {name} ({manifest_mode}, base={base_model})")


def hash_name(name):
    # Stable per-directory gguf seed, dependency-free.
    return int(hashlib.sha256(name.encode("utf-8")).hexdigest()[:8], 16)


def main():
    os.makedirs(HERE, exist_ok=True)
    _generate("valid", CONFIGURED_BASE, inert=False, manifest_mode="ok")
    _generate("tampered-source", CONFIGURED_BASE, inert=False, manifest_mode="tampered-source")
    _generate("tampered-gguf", CONFIGURED_BASE, inert=False, manifest_mode="tampered-gguf")
    _generate("no-manifest", CONFIGURED_BASE, inert=False, manifest_mode="none")
    _generate("base-mismatch", MISMATCH_BASE, inert=False, manifest_mode="ok")
    _generate("inert", CONFIGURED_BASE, inert=True, manifest_mode="ok")


if __name__ == "__main__":
    main()
