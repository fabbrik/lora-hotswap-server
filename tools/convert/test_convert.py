"""I/O-matrix tests for convert.py against a faked converter. No network.

Every test drives `convert()` with `FakeConverter`; the real `Converter` never
runs, so a test run neither reaches the network nor invokes the real llama.cpp
converter. `ExplodingConverter` turns any attempt to convert into a failure, so
warm-path tests prove nothing is reconverted.
"""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

import pytest

import convert as C


# ---------------------------------------------------------------- fakes


class FakeConverter:
    """In-memory stand-in for the real converter.

    Records every adapter dir it is asked to convert and writes deterministic
    fake gguf bytes derived from the source, so a warm re-run's hashes match.
    Adapters named in `unsupported` fail with a verbatim converter error.
    """

    REVISION = "f" * 40

    def __init__(self, unsupported: set[str] | None = None):
        self.unsupported = unsupported or set()
        self.converted: list[str] = []

    @property
    def revision(self) -> str:
        return self.REVISION

    def ensure_available(self) -> None:
        pass

    def run(self, source_dir, outfile) -> None:
        self.converted.append(source_dir.name)
        if source_dir.name in self.unsupported:
            raise C.ConversionError(
                f"Model FooBarForCausalLM is not supported"
            )
        source = source_dir / C.SOURCE_FILE
        outfile.write_bytes(b"GGUF-fake:" + source.read_bytes())


class ExplodingConverter:
    """Any conversion attempt is a test failure."""

    REVISION = "e" * 40

    @property
    def revision(self) -> str:
        return self.REVISION

    def ensure_available(self):  # pragma: no cover
        raise AssertionError("unexpected ensure_available")

    def run(self, *a, **k):  # pragma: no cover
        raise AssertionError("unexpected conversion")


# ---------------------------------------------------------------- fixtures

ALICE = C.Adapter(name="alice", target="adapters/alice")
BOB = C.Adapter(name="bob", target="adapters/bob")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def seed_source(root, adapter, weights: bytes = b"\x00weights"):
    d = adapter.dir_for(root)
    d.mkdir(parents=True, exist_ok=True)
    (d / C.SOURCE_FILE).write_bytes(weights)
    (d / "adapter_config.json").write_text('{"base_model_name_or_path": "x"}')


def statuses(results):
    return {r.adapter.name: r.status for r in results}


# ---------------------------------------------------------------- cold


def test_cold_conversion_writes_gguf_and_manifest(tmp_path):
    seed_source(tmp_path, ALICE, b"alice-weights")
    conv = FakeConverter()

    results = C.convert([ALICE], tmp_path, conv)

    assert statuses(results) == {"alice": C.CONVERTED}
    gguf = ALICE.gguf_path(tmp_path)
    manifest_path = ALICE.manifest_path(tmp_path)
    assert gguf.exists() and manifest_path.exists()

    manifest = json.loads(manifest_path.read_text())
    # Exactly the three fields -- adapterId is Story 1.4's, absent here.
    assert set(manifest) == {"sourceSha256", "ggufSha256", "converterRevision"}
    assert manifest["sourceSha256"] == sha(b"alice-weights")
    assert manifest["ggufSha256"] == sha(gguf.read_bytes())
    assert manifest["converterRevision"] == FakeConverter.REVISION


# ---------------------------------------------------------------- warm


def test_warm_run_is_a_no_op_and_never_reconverts(tmp_path):
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter())
    gguf_before = ALICE.gguf_path(tmp_path).read_bytes()
    manifest_before = ALICE.manifest_path(tmp_path).read_bytes()

    results = C.convert([ALICE], tmp_path, ExplodingConverter())

    assert statuses(results) == {"alice": C.UNCHANGED}
    assert all(r.ok for r in results)
    assert ALICE.gguf_path(tmp_path).read_bytes() == gguf_before
    assert ALICE.manifest_path(tmp_path).read_bytes() == manifest_before


# ---------------------------------------------------------------- unsupported


def test_unsupported_adapter_names_it_with_verbatim_error(tmp_path):
    seed_source(tmp_path, ALICE)
    results = C.convert([ALICE], tmp_path, FakeConverter(unsupported={"alice"}))

    assert statuses(results) == {"alice": C.FAILED}
    detail = results[0].detail
    assert "alice" in detail
    assert "FooBarForCausalLM is not supported" in detail  # converter's own words


def test_unsupported_adapter_writes_no_partial_artifacts(tmp_path):
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter(unsupported={"alice"}))

    assert not ALICE.gguf_path(tmp_path).exists()
    assert not ALICE.manifest_path(tmp_path).exists()
    # No scratch dir left behind either.
    leftovers = [p.name for p in ALICE.dir_for(tmp_path).iterdir()]
    assert set(leftovers) == {C.SOURCE_FILE, "adapter_config.json"}


def test_one_unsupported_adapter_does_not_stop_the_others(tmp_path):
    seed_source(tmp_path, ALICE)
    seed_source(tmp_path, BOB)
    results = C.convert([ALICE, BOB], tmp_path, FakeConverter(unsupported={"alice"}))

    assert statuses(results) == {"alice": C.FAILED, "bob": C.CONVERTED}
    assert BOB.gguf_path(tmp_path).exists()


# ---------------------------------------------------------------- modified


def test_modified_gguf_is_reported_and_never_overwritten(tmp_path):
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter())
    ALICE.gguf_path(tmp_path).write_bytes(b"tampered")

    results = C.convert([ALICE], tmp_path, ExplodingConverter())

    assert statuses(results) == {"alice": C.MODIFIED}
    assert not results[0].ok
    assert C.GGUF_FILE in results[0].detail and "sha256" in results[0].detail
    assert ALICE.gguf_path(tmp_path).read_bytes() == b"tampered"  # untouched


def test_modified_source_is_reported(tmp_path):
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter())
    ALICE.source_path(tmp_path).write_bytes(b"different-weights")

    results = C.convert([ALICE], tmp_path, ExplodingConverter())

    assert statuses(results) == {"alice": C.MODIFIED}
    assert C.SOURCE_FILE in results[0].detail


def test_manifest_present_but_gguf_missing_is_modified(tmp_path):
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter())
    ALICE.gguf_path(tmp_path).unlink()

    results = C.convert([ALICE], tmp_path, ExplodingConverter())

    assert statuses(results) == {"alice": C.MODIFIED}
    assert C.GGUF_FILE in results[0].detail


def test_gguf_present_but_manifest_missing_is_modified(tmp_path):
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter())
    ALICE.manifest_path(tmp_path).unlink()

    results = C.convert([ALICE], tmp_path, ExplodingConverter())

    assert statuses(results) == {"alice": C.MODIFIED}
    assert C.MANIFEST_FILE in results[0].detail


# ---------------------------------------------------------------- partial set


def test_partial_set_converts_only_the_missing_ones(tmp_path):
    seed_source(tmp_path, ALICE)
    seed_source(tmp_path, BOB)
    C.convert([ALICE], tmp_path, FakeConverter())  # alice done, bob not

    conv = FakeConverter()
    results = C.convert([ALICE, BOB], tmp_path, conv)

    assert statuses(results) == {"alice": C.UNCHANGED, "bob": C.CONVERTED}
    assert conv.converted == ["bob"]  # alice not reconverted


# ---------------------------------------------------------------- missing source


def test_missing_source_fails_naming_the_adapter(tmp_path):
    # Dir with a config but no safetensors -- not acquired yet.
    d = ALICE.dir_for(tmp_path)
    d.mkdir(parents=True)
    (d / "adapter_config.json").write_text("{}")

    results = C.convert([ALICE], tmp_path, ExplodingConverter())

    assert statuses(results) == {"alice": C.FAILED}
    assert "alice" in results[0].detail


# ---------------------------------------------------------------- exit codes


def test_process_exits_nonzero_iff_any_adapter_failed(tmp_path, monkeypatch):
    seed_source(tmp_path, ALICE)
    seed_source(tmp_path, BOB)
    conv = FakeConverter(unsupported={"bob"})
    monkeypatch.setattr(C, "load_adapters", lambda *a: [ALICE, BOB])
    monkeypatch.setattr(
        C, "load_converter_lock", lambda: {"revision": conv.revision}
    )
    monkeypatch.setattr(C, "Converter", lambda lock: conv)

    assert C.main(["--root", str(tmp_path)]) == C.EXIT_FAILED


def test_process_exits_zero_when_all_succeed(tmp_path, monkeypatch):
    seed_source(tmp_path, ALICE)
    conv = FakeConverter()
    monkeypatch.setattr(C, "load_adapters", lambda *a: [ALICE])
    monkeypatch.setattr(
        C, "load_converter_lock", lambda: {"revision": conv.revision}
    )
    monkeypatch.setattr(C, "Converter", lambda lock: conv)

    assert C.main(["--root", str(tmp_path)]) == C.EXIT_OK


def test_report_gives_modified_its_own_wording_distinct_from_failed():
    import io

    modified = C.Result(ALICE, C.MODIFIED, "adapter.gguf sha256 x != y")
    buf = io.StringIO()
    C.report([modified], out=buf)
    text = buf.getvalue()
    # A MODIFIED artifact is a hash mismatch, not a conversion failure: distinct
    # wording, and never the "failed to convert / re-run" message.
    assert "disagrees with its recorded hashes" in text
    assert "delete" in text  # tells the operator how to recover
    assert "failed to convert" not in text


def test_report_keeps_failed_message_for_real_failures():
    import io

    failed = C.Result(ALICE, C.FAILED, "converter error")
    buf = io.StringIO()
    C.report([failed], out=buf)
    text = buf.getvalue()
    assert "failed to convert" in text
    assert "disagrees with its recorded hashes" not in text


def test_main_does_not_echo_modified_to_stderr_but_still_exits_nonzero(
    tmp_path, monkeypatch, capsys
):
    # A MODIFIED adapter must not be double-reported to the stderr error stream
    # (which is reserved for genuine conversion failures), yet still exits nonzero.
    seed_source(tmp_path, ALICE)
    conv = FakeConverter()
    C.convert([ALICE], tmp_path, conv)
    ALICE.gguf_path(tmp_path).write_bytes(b"tampered")

    monkeypatch.setattr(C, "load_adapters", lambda *a: [ALICE])
    monkeypatch.setattr(
        C, "load_converter_lock", lambda: {"revision": conv.revision}
    )
    # A MODIFIED adapter never reaches the converter (warm path); this fake's
    # ensure_available is a no-op and its run() would fail the test if called.
    monkeypatch.setattr(C, "Converter", lambda lock: conv)

    assert C.main(["--root", str(tmp_path)]) == C.EXIT_FAILED
    # main()'s `print(..., file=sys.stderr)` binds sys.stderr at call time, so
    # capsys sees it -- and it must be empty of the modified adapter.
    assert "error: alice" not in capsys.readouterr().err


class UnobtainableConverter:
    """A converter whose pinned revision cannot be fetched."""

    REVISION = "a" * 40

    @property
    def revision(self) -> str:
        return self.REVISION

    def ensure_available(self) -> None:
        raise C.ConversionError(
            f"converter revision {self.REVISION} could not be fetched from origin"
        )

    def run(self, *a, **k):  # pragma: no cover
        raise AssertionError("run must never be reached when the converter is unobtainable")


def test_unobtainable_converter_exits_nonzero_and_leaves_existing_artifacts(
    tmp_path, monkeypatch, capsys
):
    # ALICE is cold (needs conversion) so the converter IS obtained -> fails fast.
    # BOB is already current; the fail-fast happens before the convert loop, so
    # BOB's existing artifacts are left byte-for-byte untouched.
    seed_source(tmp_path, BOB)
    C.convert([BOB], tmp_path, FakeConverter())
    bob_gguf = BOB.gguf_path(tmp_path).read_bytes()
    bob_manifest = BOB.manifest_path(tmp_path).read_bytes()
    seed_source(tmp_path, ALICE)  # cold: source only, no gguf/manifest

    conv = UnobtainableConverter()
    monkeypatch.setattr(C, "load_adapters", lambda *a: [ALICE, BOB])
    monkeypatch.setattr(
        C, "load_converter_lock", lambda: {"revision": conv.revision}
    )
    monkeypatch.setattr(C, "Converter", lambda lock: conv)

    assert C.main(["--root", str(tmp_path)]) == C.EXIT_FAILED
    # The revision that failed is named on stderr, and nothing was converted.
    assert conv.REVISION in capsys.readouterr().err
    assert not ALICE.gguf_path(tmp_path).exists()
    assert not ALICE.manifest_path(tmp_path).exists()
    assert BOB.gguf_path(tmp_path).read_bytes() == bob_gguf
    assert BOB.manifest_path(tmp_path).read_bytes() == bob_manifest


def test_fully_warm_run_never_obtains_the_converter(tmp_path, monkeypatch):
    # Every adapter already current -> ensure_available must not be called at all
    # (a fully-warm run must not touch git/network). UnobtainableConverter would
    # raise if ensure_available were reached.
    seed_source(tmp_path, ALICE)
    C.convert([ALICE], tmp_path, FakeConverter())

    conv = UnobtainableConverter()
    monkeypatch.setattr(C, "load_adapters", lambda *a: [ALICE])
    monkeypatch.setattr(
        C, "load_converter_lock", lambda: {"revision": conv.revision}
    )
    monkeypatch.setattr(C, "Converter", lambda lock: conv)

    assert C.main(["--root", str(tmp_path)]) == C.EXIT_OK


# ---------------------------------------------------------------- sources / lock


def test_load_adapters_reads_the_four_from_sources_toml():
    adapters = C.load_adapters()
    assert {a.name for a in adapters} == {
        "self_cognition_Alice",
        "self_cognition_Bob",
        "Qwen3-0.6B-TLDR-Lora",
        "qwen3_06B_lora_math",
    }
    # The base-model artifact is not an adapter and must be excluded.
    assert all("GGUF" not in a.name or "Lora" in a.name for a in adapters)


def test_committed_converter_lock_pins_a_full_commit_id():
    lock = C.load_converter_lock()
    assert len(lock["revision"]) == 40
    assert lock["script"] == "convert_lora_to_gguf.py"


def test_malformed_sources_entry_raises_named_conversion_error(tmp_path):
    src = tmp_path / "sources.toml"
    # kind=adapter but no `target` -- must be a named ConversionError, not KeyError.
    src.write_text('[[artifact]]\nname = "x"\nkind = "adapter"\n')
    with pytest.raises(C.ConversionError) as exc:
        C.load_adapters(src)
    assert "target" in str(exc.value)


# ---------------------------------------------------------------- empty output


def test_zero_byte_converter_output_fails_with_no_manifest(tmp_path, monkeypatch):
    # A converter that exits 0 but writes an empty/truncated file must not be
    # hashed and published as a valid gguf.
    seed_source(tmp_path, ALICE)
    lock = {
        "repo": "x",
        "revision": "a" * 40,
        "script": "convert_lora_to_gguf.py",
        "paths": ["convert_lora_to_gguf.py"],
    }
    converter = C.Converter(lock, vendor_dir=tmp_path / "vendor")
    converter._ready = True  # skip real vendoring

    def fake_run(cmd, **kwargs):
        # Emulate the converter: create the outfile but leave it empty, exit 0.
        outfile = Path(cmd[cmd.index("--outfile") + 1])
        outfile.write_bytes(b"")

        class P:
            returncode = 0
            stderr = ""
            stdout = ""

        return P()

    monkeypatch.setattr(C.subprocess, "run", fake_run)

    results = C.convert([ALICE], tmp_path, converter)

    assert statuses(results) == {"alice": C.FAILED}
    assert "empty file" in results[0].detail
    assert not ALICE.gguf_path(tmp_path).exists()
    assert not ALICE.manifest_path(tmp_path).exists()


def _ready_converter(tmp_path):
    lock = {
        "repo": "x",
        "revision": "a" * 40,
        "script": "convert_lora_to_gguf.py",
        "paths": ["convert_lora_to_gguf.py"],
    }
    converter = C.Converter(lock, vendor_dir=tmp_path / "vendor")
    converter._ready = True  # skip real vendoring
    return converter


def _proc(returncode, stderr="", stdout=""):
    class P:
        pass

    P.returncode = returncode
    P.stderr = stderr
    P.stdout = stdout
    return P()


def test_real_converter_run_surfaces_verbatim_stderr_on_nonzero_exit(
    tmp_path, monkeypatch
):
    # Exercises the REAL Converter.run non-zero branch (not FakeConverter): the
    # converter's own stderr must reach the operator verbatim.
    converter = _ready_converter(tmp_path)
    monkeypatch.setattr(
        C.subprocess,
        "run",
        lambda cmd, **kw: _proc(1, stderr="Model FooBarForCausalLM is not supported"),
    )
    with pytest.raises(C.ConversionError) as exc:
        converter.run(tmp_path, tmp_path / "out.gguf")
    assert str(exc.value) == "Model FooBarForCausalLM is not supported"


def test_real_converter_run_reports_missing_output_file(tmp_path, monkeypatch):
    # Exit 0 but no file produced -> named ConversionError (untested branch).
    converter = _ready_converter(tmp_path)
    monkeypatch.setattr(C.subprocess, "run", lambda cmd, **kw: _proc(0))
    with pytest.raises(C.ConversionError) as exc:
        converter.run(tmp_path, tmp_path / "out.gguf")
    assert "produced no file" in str(exc.value)


def test_real_converter_run_rejects_non_gguf_output(tmp_path, monkeypatch):
    # Exit 0 with a non-empty but non-GGUF file must be rejected before hashing.
    converter = _ready_converter(tmp_path)

    def fake_run(cmd, **kw):
        Path(cmd[cmd.index("--outfile") + 1]).write_bytes(b"not-a-gguf-file")
        return _proc(0)

    monkeypatch.setattr(C.subprocess, "run", fake_run)
    with pytest.raises(C.ConversionError) as exc:
        converter.run(tmp_path, tmp_path / "out.gguf")
    assert "non-GGUF" in str(exc.value)


def test_load_converter_lock_rejects_non_canonical_revision(tmp_path):
    # A tag/branch (not a 40-hex commit) must be refused: the manifest's
    # "canonical commit id verbatim" invariant is enforced in code, not test-only.
    path = tmp_path / "converter.lock.json"
    path.write_text(json.dumps({
        "repo": "x", "revision": "main",
        "script": "convert_lora_to_gguf.py", "paths": ["convert_lora_to_gguf.py"],
    }))
    with pytest.raises(C.ConversionError) as exc:
        C.load_converter_lock(path)
    assert "40-hex" in str(exc.value)
