#!/usr/bin/env python3
"""Write a Python PEFT merge reference next to every acquired adapter (Story 1.9).

The running instance verifies an admitted adapter against
``adapters/<name>/merge_reference.safetensors``. This tool produces that file from
the real base model and the real adapter, using PEFT's own ``merge_and_unload`` as
ground truth. It follows rung 1's ``make_real_reference.py`` recipe and runs in
rung 1's locked environment rather than a second one:

    cd lora-hotswap-server
    uv run --project ../lora-anatomy-dotnet/reference python tools/reference/make_references.py

A full reference would hold ``w0`` and ``w_merged`` for every targeted linear layer
(gigabytes per adapter), so only a deterministic sample of ``N_TARGETS`` modules
(default 6), spread evenly over the sorted target list, is written. The verifier checks
every module the reference covers.

Offline once ``BASE_ID`` is in the Hugging Face cache. The default,
``unsloth/Qwen3-0.6B``, is the mirror of Qwen3-0.6B that rung 1 used; ``Qwen/Qwen3-0.6B``
works too once downloaded.
References are derived artifacts and live under the gitignored ``adapters/`` tree.
"""

import os
import sys
from pathlib import Path

import torch
from peft import PeftModel
from safetensors.torch import save_file
from transformers import AutoModelForCausalLM

ROOT = Path(__file__).resolve().parents[2]
ADAPTERS = ROOT / "adapters"
BASE_ID = os.environ.get("BASE_ID", "unsloth/Qwen3-0.6B")
N_TARGETS = int(os.environ.get("N_TARGETS", "6"))
REFERENCE = "merge_reference.safetensors"

PEFT_PREFIX = "base_model.model."
LORA_A = ".lora_A.default.weight"


def spread(items, n):
    """n evenly spaced items, first and last included, in order."""
    if len(items) <= n:
        return list(items)
    step = (len(items) - 1) / (n - 1)
    return [items[round(i * step)] for i in range(n)]


def make_reference(adapter_dir: Path) -> None:
    base = AutoModelForCausalLM.from_pretrained(BASE_ID, dtype=torch.float32).eval()
    peft_model = PeftModel.from_pretrained(base, str(adapter_dir))

    targets = sorted(
        n[len(PEFT_PREFIX):-len(LORA_A)]
        for n, _ in peft_model.named_parameters()
        if n.endswith(LORA_A)
    )
    chosen = spread(targets, N_TARGETS)

    # PeftModel wraps each target in a LoraLayer: the original weight is at
    # <module>.base_layer.weight until merge_and_unload.
    w0 = {
        t: peft_model.get_submodule(f"{PEFT_PREFIX}{t}.base_layer").weight.detach().clone()
        for t in chosen
    }
    merged = peft_model.merge_and_unload()

    tensors = {}
    for t in chosen:
        wm = merged.get_submodule(t).weight.detach().clone()
        delta = (wm - w0[t]).abs().max().item()
        if delta <= 1e-5:
            raise SystemExit(f"{adapter_dir.name}: delta {delta:.3e} for {t} is within tolerance -- nothing to verify")
        tensors[f"{t}.w0"] = w0[t].contiguous()
        tensors[f"{t}.w_merged"] = wm.contiguous()
        print(f"  {t}: max |delta| = {delta:.6e}")

    save_file(tensors, str(adapter_dir / REFERENCE), metadata={"base": BASE_ID, "targets": ",".join(chosen)})
    print(f"{adapter_dir.name}: {len(chosen)} of {len(targets)} targeted modules -> {REFERENCE}")


def main() -> int:
    dirs = sorted(d for d in ADAPTERS.iterdir() if (d / "adapter_model.safetensors").is_file())
    if not dirs:
        print(f"no adapters under {ADAPTERS}; run tools/acquire first", file=sys.stderr)
        return 1
    torch.manual_seed(0)
    for d in dirs:
        make_reference(d)
    return 0


if __name__ == "__main__":
    sys.exit(main())
