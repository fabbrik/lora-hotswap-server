#!/usr/bin/env python3
"""Convert acquired PEFT LoRA adapters to GGUF with a provenance manifest.

    cd lora-hotswap-server/tools/convert && uv run convert.py

For each adapter declared in `tools/acquire/sources.toml` (`kind = "adapter"`),
obtain a pinned copy of llama.cpp's `convert_lora_to_gguf.py`, run it to produce
`adapter.gguf`, and write a sidecar `adapter.manifest.json` binding the source
safetensors hash, the produced GGUF hash, and the converter revision.

This is the *only* link between an adapter's verification form (safetensors,
what Story 1.6 checks) and its serving form (GGUF, what LLamaSharp loads). The
manifest records four fields -- `sourceSha256`, `ggufSha256`, the converter
revision, and `adapterId`, the content-derived identity (`sha256:` + the SHA-256
of the source safetensors) that logs, errors, and responses name an adapter by.

Idempotency is verification, not existence: a warm re-run hashes the on-disk
source and gguf against the manifest and reconverts nothing when they match. A
file whose hash disagrees with its manifest -- or a manifest missing or
disagreeing on `adapterId` -- is reported (named) as modified, never silently
reconverted, migrated, or overwritten.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
import tomllib
from dataclasses import dataclass
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parents[1]
SOURCES_PATH = REPO_ROOT / "tools" / "acquire" / "sources.toml"
CONVERTER_LOCK_PATH = TOOL_DIR / "converter.lock.json"
VENDOR_DIR = TOOL_DIR / "vendor" / "llama.cpp"

SOURCE_FILE = "adapter_model.safetensors"
GGUF_FILE = "adapter.gguf"
MANIFEST_FILE = "adapter.manifest.json"

EXIT_OK = 0
EXIT_FAILED = 1

# Subprocess timeouts (seconds). Generous: an unattended run must not hang
# forever on a stuck fetch or converter, but conversion is genuinely slow.
GIT_TIMEOUT_S = 300  # 5 min for a shallow fetch/checkout of the converter
CONVERT_TIMEOUT_S = 1800  # 30 min per adapter; CPU-only conversion is slow

# Status vocabulary reported per adapter.
CONVERTED = "converted"
UNCHANGED = "already-current"
MODIFIED = "modified"
FAILED = "failed"


class ConversionError(Exception):
    """A named, per-adapter failure. Message is the operator-facing text."""


@dataclass(frozen=True)
class Adapter:
    name: str
    target: str

    def dir_for(self, root: Path) -> Path:
        return root / self.target

    def source_path(self, root: Path) -> Path:
        return self.dir_for(root) / SOURCE_FILE

    def gguf_path(self, root: Path) -> Path:
        return self.dir_for(root) / GGUF_FILE

    def manifest_path(self, root: Path) -> Path:
        return self.dir_for(root) / MANIFEST_FILE


@dataclass
class Result:
    adapter: Adapter
    status: str
    detail: str = ""

    @property
    def ok(self) -> bool:
        return self.status in (CONVERTED, UNCHANGED)


# --------------------------------------------------------------------------
# Converter layer. Kept behind this narrow surface so the test suite can fake
# it and never touch the network or invoke the real llama.cpp converter.
# --------------------------------------------------------------------------


class Converter:
    """Obtains and runs llama.cpp's `convert_lora_to_gguf.py` at a pinned
    revision. The only place this tool shells out or reaches the network."""

    def __init__(self, lock: dict, vendor_dir: Path = VENDOR_DIR):
        self._lock = lock
        self._vendor_dir = vendor_dir
        self._ready = False

    @property
    def revision(self) -> str:
        """The full canonical VCS commit id copied verbatim into each manifest."""
        return self._lock["revision"]

    def ensure_available(self) -> None:
        """Vendor the pinned converter reproducibly and offline-on-rerun.

        When the vendored tree already records the pinned revision, this touches
        neither git nor the network. Otherwise it fetches exactly that commit and
        checks out only the paths the converter needs.
        """
        marker = self._vendor_dir / ".revision"
        try:
            current = marker.read_text().strip() if marker.exists() else None
        except OSError:
            # An unreadable marker (e.g. it is a directory) is treated as stale
            # rather than crashing the run; the vendor tree will be re-fetched.
            current = None
        if current == self.revision:
            self._ready = True
            return

        repo = self._lock["repo"]
        revision = self.revision
        paths = self._lock["paths"]
        self._vendor_dir.parent.mkdir(parents=True, exist_ok=True)

        with tempfile.TemporaryDirectory(dir=self._vendor_dir.parent) as tmp:
            work = Path(tmp) / "work"
            work.mkdir()
            try:
                self._git(work, "init", "-q")
                self._git(work, "remote", "add", "origin", repo)
                self._git(work, "fetch", "-q", "--depth", "1", "origin", revision)
                self._git(work, "checkout", "-q", revision, "--", *paths)
            except subprocess.CalledProcessError as exc:
                raise ConversionError(
                    f"converter revision {revision} could not be fetched from "
                    f"{repo}: {exc.stderr.strip() if exc.stderr else exc}"
                ) from exc
            except subprocess.TimeoutExpired as exc:
                raise ConversionError(
                    f"converter revision {revision} could not be fetched from "
                    f"{repo}: git timed out after {GIT_TIMEOUT_S}s"
                ) from exc

            staged = Path(tmp) / "staged"
            staged.mkdir()
            for rel in paths:
                src = work / rel
                if not src.exists():
                    raise ConversionError(
                        f"converter revision {revision}: expected path {rel!r} "
                        f"absent after checkout"
                    )
                dst = staged / rel
                dst.parent.mkdir(parents=True, exist_ok=True)
                if src.is_dir():
                    shutil.copytree(src, dst)
                else:
                    shutil.copy2(src, dst)
            (staged / ".revision").write_text(revision + "\n")

            # Replace any prior vendored tree atomically-ish: remove then move.
            if self._vendor_dir.exists():
                shutil.rmtree(self._vendor_dir)
            shutil.move(str(staged), str(self._vendor_dir))
        self._ready = True

    @staticmethod
    def _git(cwd: Path, *args: str) -> None:
        subprocess.run(
            ["git", *args],
            cwd=cwd,
            check=True,
            capture_output=True,
            text=True,
            timeout=GIT_TIMEOUT_S,
        )

    def run(self, source_dir: Path, outfile: Path) -> None:
        """Run the converter, writing the GGUF to `outfile`.

        On converter failure, raise ConversionError carrying the converter's own
        stderr verbatim. Deliberately does not normalise or override the base
        model string: the converter reads each adapter's declared
        `base_model_name_or_path` and loads only that base's config from the Hub.
        """
        if not self._ready:
            self.ensure_available()
        script = self._vendor_dir / self._lock["script"]
        try:
            proc = subprocess.run(
                [
                    sys.executable,
                    str(script),
                    str(source_dir),
                    "--outfile",
                    str(outfile),
                    "--outtype",
                    "f32",
                ],
                cwd=self._vendor_dir,
                capture_output=True,
                text=True,
                timeout=CONVERT_TIMEOUT_S,
            )
        except subprocess.TimeoutExpired as exc:
            raise ConversionError(
                f"converter timed out after {CONVERT_TIMEOUT_S}s on {source_dir}"
            ) from exc
        if proc.returncode != 0:
            # Surface the converter's own error verbatim (stderr, then stdout).
            message = (proc.stderr or "").strip() or (proc.stdout or "").strip()
            raise ConversionError(message or f"converter exited {proc.returncode}")
        if not outfile.exists():
            raise ConversionError(
                f"converter reported success but produced no file at {outfile}"
            )
        if outfile.stat().st_size == 0:
            # Exit 0 with an empty/truncated file: hashing and publishing it would
            # let 1.6 admit a hollow gguf as matching its manifest.
            raise ConversionError(
                f"converter reported success but produced an empty file at {outfile}"
            )
        with outfile.open("rb") as handle:
            magic = handle.read(4)
        if magic != b"GGUF":
            # Non-empty but not a GGUF container (truncated/garbage on a spurious
            # exit 0). Reject before it is hashed and published as valid.
            raise ConversionError(
                f"converter produced a non-GGUF file at {outfile} "
                f"(leading bytes {magic!r}, expected b'GGUF')"
            )


# --------------------------------------------------------------------------
# Pure helpers
# --------------------------------------------------------------------------


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def adapter_id_of(path: Path) -> str:
    """Content-derived adapter identity: `sha256:` + the SHA-256 hex of the
    source safetensors bytes. Stable across renames and moves, never a path."""
    return "sha256:" + sha256_of(path)


def sources_path_for(root: Path) -> Path:
    return root / "tools" / "acquire" / "sources.toml"


def load_adapters(path: Path = SOURCES_PATH) -> list[Adapter]:
    """Read the adapter set from sources.toml as data (never rescan dirs)."""
    data = tomllib.loads(path.read_text())
    adapters: list[Adapter] = []
    for entry in data.get("artifact", []):
        if entry.get("kind") != "adapter":
            continue
        for key in ("name", "target"):
            if key not in entry:
                raise ConversionError(f"{path}: adapter entry missing {key!r}")
        adapters.append(Adapter(name=entry["name"], target=entry["target"]))
    if not adapters:
        raise ConversionError(f"{path}: declares no adapters")
    return adapters


def load_converter_lock(path: Path = CONVERTER_LOCK_PATH) -> dict:
    lock = json.loads(path.read_text())
    for key in ("repo", "revision", "script", "paths"):
        if not lock.get(key):
            raise ConversionError(f"{path}: missing {key!r}")
    revision = lock["revision"]
    # Enforce the "full canonical commit id, verbatim" invariant in code, not
    # just in a test: a tag/branch would fetch and check out fine but record a
    # non-canonical provenance string into every manifest.
    if not (
        isinstance(revision, str)
        and len(revision) == 40
        and all(c in "0123456789abcdef" for c in revision.lower())
    ):
        raise ConversionError(
            f"{path}: 'revision' must be a full 40-hex commit id, got {revision!r}"
        )
    if not isinstance(lock["paths"], list):
        raise ConversionError(f"{path}: 'paths' must be a list")
    return lock


def build_manifest(
    source_sha: str, gguf_sha: str, revision: str, adapter_id: str
) -> dict:
    # Four fields. `adapterId` (Story 1.4) is the content-derived identity token;
    # `sourceSha256` remains the bare provenance hash. sort_keys renders order.
    return {
        "sourceSha256": source_sha,
        "ggufSha256": gguf_sha,
        "converterRevision": revision,
        "adapterId": adapter_id,
    }


def render_manifest(manifest: dict) -> str:
    return json.dumps(manifest, indent=2, sort_keys=True) + "\n"


def read_manifest(path: Path) -> dict:
    return json.loads(path.read_text())


# --------------------------------------------------------------------------
# Per-adapter conversion
# --------------------------------------------------------------------------


def verify_current(adapter: Adapter, root: Path) -> str | None:
    """Return None when the on-disk gguf + manifest are current, else a
    description of why they are not (mismatch, missing half, corrupt manifest).

    Idempotency is verification, not existence: the manifest's recorded hashes
    are checked against the bytes actually on disk.
    """
    gguf = adapter.gguf_path(root)
    manifest_path = adapter.manifest_path(root)
    if not manifest_path.exists() and not gguf.exists():
        return None  # cold: nothing converted yet
    if not manifest_path.exists():
        return f"{GGUF_FILE} present but {MANIFEST_FILE} is missing"
    if not gguf.exists():
        return f"{MANIFEST_FILE} present but {GGUF_FILE} is missing"

    try:
        manifest = read_manifest(manifest_path)
    except (OSError, ValueError) as exc:
        return f"{MANIFEST_FILE} is unreadable: {exc}"

    # The source-missing case is handled and named up front in convert_one, so
    # the warm path is only reached with the source present.
    source = adapter.source_path(root)
    problems: list[str] = []
    actual_source = sha256_of(source)
    source_matches = actual_source == manifest.get("sourceSha256")
    if not source_matches:
        problems.append(
            f"{SOURCE_FILE} sha256 {actual_source} does not match manifest "
            f"{manifest.get('sourceSha256')}"
        )
    actual_gguf = sha256_of(gguf)
    if actual_gguf != manifest.get("ggufSha256"):
        problems.append(
            f"{GGUF_FILE} sha256 {actual_gguf} does not match manifest "
            f"{manifest.get('ggufSha256')}"
        )
    # `adapterId` is the content-derived identity added in Story 1.4. Checked only
    # once the source hash matches (spec: "after source+gguf match") -- a source
    # mismatch is already reported, and adapterId is derived from the source, so
    # reporting it too would be redundant. A manifest lacking adapterId (pre-1.4)
    # or disagreeing with it is a disagreement, reported (named), never migrated.
    if source_matches:
        actual_id = "sha256:" + actual_source  # reuse the hash already computed
        manifest_id = manifest.get("adapterId")
        if manifest_id is None:
            problems.append(
                f"manifest is missing adapterId (expected {actual_id}); "
                f"reconvert to add it"
            )
        elif manifest_id != actual_id:
            problems.append(
                f"manifest adapterId {manifest_id} does not match id "
                f"{actual_id} recomputed from {SOURCE_FILE}"
            )
    if problems:
        return "; ".join(problems)
    return None


def needs_conversion(adapter: Adapter, root: Path) -> bool:
    """True when a cold conversion is required: the source is present and no
    gguf/manifest exists yet. Warm ("already-current") and MODIFIED states never
    need the converter, so a fully-warm run must not obtain it (no git/network).
    """
    if not adapter.source_path(root).exists():
        return False
    return (
        not adapter.gguf_path(root).exists()
        and not adapter.manifest_path(root).exists()
    )


def convert_one(adapter: Adapter, root: Path, converter) -> Result:
    source = adapter.source_path(root)
    if not source.exists():
        raise ConversionError(
            f"{adapter.name}: {SOURCE_FILE} not found at {source} -- acquire first"
        )

    gguf = adapter.gguf_path(root)
    manifest_path = adapter.manifest_path(root)

    # Warm path: something is already on disk. Verify, never overwrite.
    if gguf.exists() or manifest_path.exists():
        problem = verify_current(adapter, root)
        if problem is None:
            return Result(adapter, UNCHANGED)
        return Result(adapter, MODIFIED, problem)

    # Cold path: convert into a scratch dir, then move the finished artifact into
    # place so a failed or partial conversion never lands a half-written .gguf.
    adapter_dir = adapter.dir_for(root)
    with tempfile.TemporaryDirectory(dir=adapter_dir) as tmp:
        tmp_gguf = Path(tmp) / GGUF_FILE
        try:
            converter.run(adapter_dir, tmp_gguf)
        except ConversionError as exc:
            # Name the adapter; keep the converter's own error verbatim.
            raise ConversionError(f"{adapter.name}: {exc}") from exc

        source_sha = sha256_of(source)
        gguf_sha = sha256_of(tmp_gguf)
        # Derive the id from the hash already in hand (byte-consistent with
        # sourceSha256); no second read of the source. Format matches adapter_id_of.
        adapter_id = "sha256:" + source_sha
        manifest = build_manifest(
            source_sha, gguf_sha, converter.revision, adapter_id
        )

        tmp_manifest = Path(tmp) / MANIFEST_FILE
        tmp_manifest.write_text(render_manifest(manifest))

        # Both artifacts are complete in the scratch dir; publish them together.
        shutil.move(str(tmp_gguf), str(gguf))
        shutil.move(str(tmp_manifest), str(manifest_path))

    return Result(adapter, CONVERTED)


def convert(adapters: list[Adapter], root: Path, converter) -> list[Result]:
    results: list[Result] = []
    for adapter in adapters:
        try:
            results.append(convert_one(adapter, root, converter))
        except ConversionError as exc:
            results.append(Result(adapter, FAILED, str(exc)))
        except OSError as exc:
            # Disk full, permissions, a failed move -- one adapter's I/O error
            # must not abort the batch or dump a raw traceback.
            results.append(Result(adapter, FAILED, f"{adapter.name}: {exc}"))
    return results


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def report(results: list[Result], out=sys.stdout) -> None:
    width = max((len(r.adapter.name) for r in results), default=0)
    for result in results:
        print(f"  {result.adapter.name:<{width}}  {result.status}", file=out)
        if result.detail:
            print(f"      {result.detail}", file=out)
    converted = [r for r in results if r.status == CONVERTED]
    failed = [r for r in results if r.status == FAILED]
    modified = [r for r in results if r.status == MODIFIED]
    if failed:
        print(
            f"\n{len(failed)} adapter(s) failed to convert. Nothing partial was "
            "written; resolve the adapters named above and re-run.",
            file=out,
        )
    if modified:
        print(
            f"\n{len(modified)} adapter(s) have an on-disk adapter.gguf or "
            "adapter.manifest.json that disagrees with its recorded hashes; each "
            "was left untouched. To rebuild one, delete that adapter's "
            "adapter.gguf and adapter.manifest.json and re-run.",
            file=out,
        )
    if not failed and not modified:
        if converted:
            print(f"\n{len(converted)} adapter(s) converted.", file=out)
        else:
            print("\nNo changes.", file=out)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Convert acquired PEFT LoRA adapters to GGUF with a "
        "provenance manifest."
    )
    parser.add_argument(
        "--root",
        type=Path,
        default=REPO_ROOT,
        help="repository root the adapter target paths are relative to",
    )
    args = parser.parse_args(argv)

    try:
        adapters = load_adapters(sources_path_for(args.root))
        lock = load_converter_lock()
    except (ConversionError, OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_FAILED

    converter = Converter(lock)
    # Obtain the converter only when at least one adapter actually needs a cold
    # conversion; a fully-warm run must never touch git or the network. Fail fast
    # here so an unobtainable converter is one clean top-level error, not a
    # per-adapter failure spray. OSError (disk/permission during vendoring) is
    # caught too, so it reports cleanly instead of dumping a raw traceback.
    if any(needs_conversion(a, args.root) for a in adapters):
        try:
            converter.ensure_available()
        except (ConversionError, OSError) as exc:
            print(f"error: {exc}", file=sys.stderr)
            return EXIT_FAILED

    results = convert(adapters, args.root, converter)
    report(results)

    # MODIFIED artifacts are reported (and named) by report(); re-running will
    # not fix a hash mismatch, so only genuine conversion FAILEDs are echoed to
    # the stderr error stream. Both keep the exit code non-zero.
    if any(not r.ok for r in results):
        for result in results:
            if result.status == FAILED:
                print(
                    f"error: {result.adapter.name}: {result.detail}",
                    file=sys.stderr,
                )
        return EXIT_FAILED
    return EXIT_OK


if __name__ == "__main__":
    raise SystemExit(main())
