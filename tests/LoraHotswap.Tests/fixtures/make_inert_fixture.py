#!/usr/bin/env python3
"""Generate compact, offline regression fixtures for adapter content validation.

Dependency-free: standard-library ``struct`` and ``json`` only -- no numpy, no
torch, no network. Writes two sibling adapter directories:

  inert-ybelkada-opt-350m/  reproduces the defining structure of the published
                            ``ybelkada/opt-350m-lora`` adapter (r=16, alpha=32,
                            q_proj+v_proj on facebook/opt-350m): every ``lora_B``
                            tensor is exactly zero (F32) while ``lora_A`` is
                            seeded-nonzero. Merging it is a silent no-op.
  trained-control/          the same shape but with nonzero ``lora_B`` -- a valid
                            trained adapter, so the validator must NOT flag it.

The real adapter is a gated ~700MB Hub download; committing this compact seeded
reproduction keeps the suite deterministic and network-free while still asserting
the exact real-world trap the epic guards against.

Deterministic: re-running rewrites byte-identical files.
"""

import json
import struct
import os

HERE = os.path.dirname(os.path.abspath(__file__))

R = 16
ALPHA = 32
BASE_MODEL = "facebook/opt-350m"

# A representative subset (4 modules -> 8 tensors) of opt-350m's q/v projections.
# opt-350m hidden size is 1024; keep dims tiny but shape-faithful (d = k = 1024).
IN_FEATURES = 1024
OUT_FEATURES = 1024

MODULES = [
    "base_model.model.model.decoder.layers.0.self_attn.q_proj",
    "base_model.model.model.decoder.layers.0.self_attn.v_proj",
    "base_model.model.model.decoder.layers.1.self_attn.q_proj",
    "base_model.model.model.decoder.layers.1.self_attn.v_proj",
]


def _lcg_floats(seed, count):
    """A tiny deterministic PRNG (no numpy). Returns count floats in [-0.05, 0.05]."""
    state = seed & 0xFFFFFFFF
    out = []
    for _ in range(count):
        # Numerical Recipes LCG constants.
        state = (1664525 * state + 1013904223) & 0xFFFFFFFF
        out.append((state / 0xFFFFFFFF) * 0.1 - 0.05)
    return out


def _f32_bytes(values):
    return struct.pack("<" + "f" * len(values), *values)


def _write_safetensors(path, tensors):
    """tensors: list of (name, shape, float_values). Writes an F32 safetensors."""
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
    # Sorted keys keep the header byte-identical across runs.
    header_json = json.dumps(header, sort_keys=True, separators=(",", ":")).encode("utf-8")
    with open(path, "wb") as f:
        f.write(struct.pack("<Q", len(header_json)))
        f.write(header_json)
        f.write(blob)


def _write_config(path):
    # Reproduces the published adapter's config shape. Note: NO "use_rslora" key
    # (older configs omit it) -- exercises the default-false parse path.
    config = {
        "base_model_name_or_path": BASE_MODEL,
        "bias": "none",
        "lora_alpha": ALPHA,
        "lora_dropout": 0.0,
        "peft_type": "LORA",
        "r": R,
        "target_modules": ["q_proj", "v_proj"],
        "task_type": "CAUSAL_LM",
    }
    with open(path, "w", newline="\n") as f:
        json.dump(config, f, indent=2, sort_keys=True)
        f.write("\n")


def _tensors(b_seed_base):
    """Build (lora_A nonzero, lora_B per b_seed_base) tensor list for all modules.

    b_seed_base is None -> lora_B all zero (inert); otherwise seeded-nonzero.
    """
    tensors = []
    for i, module in enumerate(MODULES):
        a_name = f"{module}.lora_A.weight"
        b_name = f"{module}.lora_B.weight"
        # lora_A: (r, in_features). Always seeded-nonzero.
        a_vals = _lcg_floats(seed=1000 + i, count=R * IN_FEATURES)
        tensors.append((a_name, (R, IN_FEATURES), a_vals))
        # lora_B: (out_features, r).
        n_b = OUT_FEATURES * R
        if b_seed_base is None:
            b_vals = [0.0] * n_b
        else:
            b_vals = _lcg_floats(seed=b_seed_base + i, count=n_b)
        tensors.append((b_name, (OUT_FEATURES, R), b_vals))
    return tensors


def _generate(dir_name, inert):
    out_dir = os.path.join(HERE, dir_name)
    os.makedirs(out_dir, exist_ok=True)
    _write_config(os.path.join(out_dir, "adapter_config.json"))
    _write_safetensors(
        os.path.join(out_dir, "adapter_model.safetensors"),
        _tensors(b_seed_base=None if inert else 5000),
    )
    print(f"wrote {dir_name} ({'inert: all lora_B zero' if inert else 'trained: nonzero lora_B'})")


def main():
    _generate("inert-ybelkada-opt-350m", inert=True)
    _generate("trained-control", inert=False)


if __name__ == "__main__":
    main()
