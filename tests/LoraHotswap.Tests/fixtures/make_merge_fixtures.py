#!/usr/bin/env python3
"""Generate the Python PEFT merge-reference fixtures for Story 1.7.

Follows rung 1's ground-truth recipe (``lora-anatomy-dotnet/reference/
make_reference.py``: build base, ``get_peft_model``, perturb ``B``, ``merge_and_unload``)
with its own fixture layout, and runs in rung 1's locked environment rather than a
second one:

    cd lora-hotswap-server/tests/LoraHotswap.Tests/fixtures
    uv run --project ../../../../lora-anatomy-dotnet/reference python make_merge_fixtures.py

Offline: the base model is a tiny random-weight Llama built from a config, so no
download is needed. The adapter and the merged weights still come from real PEFT
(``save_pretrained`` and ``merge_and_unload``), so tensor naming, matrix
orientation and alpha/r scaling are PEFT's own conventions, not ours.

Directories produced under ``merge/`` (each well under 1 MB):

  scaling-4/  r=8, lora_alpha=32 -> scaling 4. An implementation that omits
              scaling diverges from this reference.
  scaling-1/  r=8, lora_alpha=8  -> scaling 1. Omitting scaling is INVISIBLE
              here; this fixture alone cannot detect that bug.

Each directory is also admissible by the host: ``adapter_config.json`` declares
``Qwen/Qwen3-0.6B``, and like the admission fixtures it carries a tiny stand-in
``adapter.gguf`` plus an ``adapter.manifest.json`` whose hashes match the files.
``merge_reference.safetensors`` holds ``<module>.w0`` and ``<module>.w_merged``
for every targeted module -- rung 1's ground-truth key layout.

Deterministic: fixed seeds; re-running rewrites byte-identical files.
"""

import hashlib
import json
from pathlib import Path

import torch
from peft import LoraConfig, get_peft_model
from safetensors.torch import save_file
from transformers import LlamaConfig, LlamaForCausalLM

HERE = Path(__file__).resolve().parent / "merge"

CONFIGURED_BASE = "Qwen/Qwen3-0.6B"
CONVERTER_REVISION = "0" * 40
RANK = 8

TARGETS = [
    "model.layers.0.self_attn.q_proj",
    "model.layers.0.self_attn.v_proj",
    "model.layers.1.self_attn.q_proj",
    "model.layers.1.self_attn.v_proj",
]


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _generate(name: str, alpha: int, seed: int) -> None:
    out = HERE / name
    out.mkdir(parents=True, exist_ok=True)

    torch.manual_seed(seed)
    cfg = LlamaConfig(
        vocab_size=256,
        hidden_size=64,
        intermediate_size=128,
        num_hidden_layers=2,
        num_attention_heads=4,
        num_key_value_heads=4,
    )
    base = LlamaForCausalLM(cfg).eval()
    w0 = {t: base.get_submodule(t).weight.detach().clone() for t in TARGETS}

    lora_cfg = LoraConfig(
        r=RANK,
        lora_alpha=alpha,
        target_modules=["q_proj", "v_proj"],
        lora_dropout=0.0,
        bias="none",
        task_type="CAUSAL_LM",
    )
    peft_model = get_peft_model(base, lora_cfg)

    # GOTCHA (rung 1): PEFT zero-initialises lora_B, so a fresh adapter is inert
    # and the comparison would pass vacuously. Perturb B so there is a real delta.
    with torch.no_grad():
        for param_name, param in peft_model.named_parameters():
            if "lora_B" in param_name:
                param.normal_(mean=0.0, std=0.02)

    peft_model.save_pretrained(str(out))

    merged = peft_model.merge_and_unload()
    reference = {}
    for t in TARGETS:
        w_merged = merged.get_submodule(t).weight.detach().clone()
        # Same threshold as the C# verifier's tolerance: a delta within it would make the
        # suite's "changed from base" assertion fail after a clean regeneration.
        if (w_merged - w0[t]).abs().max() <= 1e-5:
            raise SystemExit(f"{name}: delta is within 1e-5 for {t} -- nothing to verify")
        reference[f"{t}.w0"] = w0[t].contiguous()
        reference[f"{t}.w_merged"] = w_merged.contiguous()
    save_file(reference, str(out / "merge_reference.safetensors"))

    # Make the fixture admissible: strict base match, then provenance files.
    config_path = out / "adapter_config.json"
    config = json.loads(config_path.read_text())
    config["base_model_name_or_path"] = CONFIGURED_BASE
    # PEFT stores target_modules as a set, whose order follows PYTHONHASHSEED.
    config["target_modules"] = sorted(config["target_modules"])
    config_path.write_text(json.dumps(config, indent=2, sort_keys=True) + "\n")

    # save_pretrained may emit a README model card; it is not part of the fixture.
    (out / "README.md").unlink(missing_ok=True)

    source = (out / "adapter_model.safetensors").read_bytes()
    gguf = b"GGUF" + hashlib.sha256(name.encode("utf-8")).digest() * 2
    (out / "adapter.gguf").write_bytes(gguf)

    source_sha = _sha256(source)
    manifest = {
        "adapterId": "sha256:" + source_sha,
        "converterRevision": CONVERTER_REVISION,
        "ggufSha256": _sha256(gguf),
        "sourceSha256": source_sha,
    }
    (out / "adapter.manifest.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    print(f"wrote {out} (alpha={alpha}, r={RANK}, scaling={alpha / RANK})")


def main() -> None:
    _generate("scaling-4", alpha=32, seed=4)
    _generate("scaling-1", alpha=8, seed=1)


if __name__ == "__main__":
    main()
