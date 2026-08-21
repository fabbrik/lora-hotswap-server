#!/usr/bin/env python3
"""Acquire the pinned base model and LoRA adapters reproducibly.

    cd lora-hotswap-server/tools/acquire && uv run acquire.py

Resolve -> download -> hash -> lock. A second run hashes what is on disk,
compares it to `acquired.lock.json`, downloads nothing and reports no changes.

This tool moves bytes and records provenance about the *download*. It does not
parse, validate, or convert adapter content -- Stories 1.3-1.5 own that -- and
the per-file `sha256` recorded here is emphatically not the content-derived
adapter identity of Story 1.4.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
import tempfile
import tomllib
from dataclasses import dataclass
from pathlib import Path

TOOL_DIR = Path(__file__).resolve().parent
REPO_ROOT = TOOL_DIR.parents[1]
SOURCES_PATH = TOOL_DIR / "sources.toml"
LOCK_PATH = TOOL_DIR / "acquired.lock.json"

LOCK_VERSION = 1

EXIT_OK = 0
EXIT_FAILED = 1

# Status vocabulary reported per artifact.
DOWNLOADED = "downloaded"
UNCHANGED = "unchanged"
MODIFIED = "modified"
FAILED = "failed"


class AcquisitionError(Exception):
    """A named, per-artifact failure. Message is the operator-facing text."""


@dataclass(frozen=True)
class Artifact:
    name: str
    kind: str
    repo_id: str
    target: str
    files: tuple[str, ...]

    def dir_for(self, root: Path) -> Path:
        return root / self.target

    def path_for(self, root: Path, filename: str) -> Path:
        return self.dir_for(root) / filename


@dataclass
class Result:
    artifact: Artifact
    status: str
    detail: str = ""

    @property
    def ok(self) -> bool:
        return self.status in (DOWNLOADED, UNCHANGED)


# --------------------------------------------------------------------------
# Hub layer. Kept behind this narrow surface so the test suite can fake it and
# never touch the network.
# --------------------------------------------------------------------------


class HuggingFaceHub:
    """The only place this tool talks to the network."""

    def resolve_revision(self, repo_id: str, revision: str | None) -> str:
        from huggingface_hub import HfApi

        info = HfApi().model_info(repo_id, revision=revision)
        sha = info.sha
        if not sha:
            raise AcquisitionError(
                f"{repo_id}: hub returned no commit sha for revision "
                f"{revision or 'main'!s}"
            )
        return sha

    def download_file(
        self, repo_id: str, filename: str, revision: str, dest: Path
    ) -> None:
        from huggingface_hub import hf_hub_download

        dest.parent.mkdir(parents=True, exist_ok=True)
        # Download into a scratch dir and move into place, so a partially
        # written file never lands at the destination path and no hub cache
        # bookkeeping directory is left inside the artifact tree.
        with tempfile.TemporaryDirectory(dir=dest.parent) as tmp:
            got = hf_hub_download(
                repo_id=repo_id,
                filename=filename,
                revision=revision,
                local_dir=tmp,
            )
            shutil.move(got, dest)


# --------------------------------------------------------------------------
# Pure helpers
# --------------------------------------------------------------------------


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_sources(path: Path = SOURCES_PATH) -> list[Artifact]:
    data = tomllib.loads(path.read_text())
    artifacts = [
        Artifact(
            name=entry["name"],
            kind=entry["kind"],
            repo_id=entry["repo_id"],
            target=entry["target"],
            files=tuple(entry["files"]),
        )
        for entry in data.get("artifact", [])
    ]
    if not artifacts:
        raise AcquisitionError(f"{path}: declares no artifacts")
    return artifacts


def load_lock(path: Path = LOCK_PATH) -> dict:
    if not path.exists():
        return {"version": LOCK_VERSION, "artifacts": {}}
    lock = json.loads(path.read_text())
    lock.setdefault("artifacts", {})
    return lock


def render_lock(lock: dict) -> str:
    return json.dumps(lock, indent=2, sort_keys=True) + "\n"


def write_lock_if_changed(lock: dict, path: Path = LOCK_PATH) -> bool:
    rendered = render_lock(lock)
    if path.exists() and path.read_text() == rendered:
        return False
    path.write_text(rendered)
    return True


# --------------------------------------------------------------------------
# Per-artifact acquisition
# --------------------------------------------------------------------------


def verify_present(artifact: Artifact, root: Path, entry: dict) -> list[str]:
    """Hash every declared file that exists and compare it to the lock.

    Returns the list of mismatch descriptions. Files that are absent are not
    a mismatch -- they are simply not acquired yet.
    """
    locked_files = entry.get("files", {})
    problems: list[str] = []
    for filename in artifact.files:
        path = artifact.path_for(root, filename)
        if not path.exists():
            continue
        locked = locked_files.get(filename)
        if locked is None:
            problems.append(f"{filename}: present on disk but absent from the lock")
            continue
        actual = sha256_of(path)
        if actual != locked.get("sha256"):
            problems.append(
                f"{filename}: sha256 {actual} does not match locked "
                f"{locked.get('sha256')}"
            )
    return problems


def missing_files(artifact: Artifact, root: Path) -> list[str]:
    return [f for f in artifact.files if not artifact.path_for(root, f).exists()]


def acquire_one(artifact: Artifact, root: Path, lock: dict, hub) -> Result:
    entry = lock["artifacts"].get(artifact.name)
    locked_revision = entry.get("revision") if entry else None

    if entry is not None:
        problems = verify_present(artifact, root, entry)
        if problems:
            detail = "; ".join(problems)
            return Result(artifact, MODIFIED, detail)
        if not missing_files(artifact, root):
            return Result(artifact, UNCHANGED)

    try:
        revision = hub.resolve_revision(artifact.repo_id, locked_revision)
    except AcquisitionError:
        raise
    except Exception as exc:  # hub errors are library-specific; name ours
        if locked_revision:
            raise AcquisitionError(
                f"{artifact.name} ({artifact.repo_id}): locked revision "
                f"{locked_revision} no longer resolves: {exc}"
            ) from exc
        raise AcquisitionError(
            f"{artifact.name} ({artifact.repo_id}): could not resolve a "
            f"revision: {exc}"
        ) from exc

    if locked_revision and revision != locked_revision:
        # A locked revision must resolve to itself. Anything else means the
        # lock no longer describes what the hub would hand us.
        raise AcquisitionError(
            f"{artifact.name} ({artifact.repo_id}): locked revision "
            f"{locked_revision} resolved to {revision}"
        )

    files_entry: dict[str, dict] = dict(entry.get("files", {})) if entry else {}
    for filename in artifact.files:
        path = artifact.path_for(root, filename)
        if path.exists():
            if filename not in files_entry:
                # Present on disk with no prior lock record (e.g. manually
                # placed, or acquired before the lock existed) -- hash it now
                # so the lock stays a complete record of what's on disk.
                files_entry[filename] = {
                    "sha256": sha256_of(path),
                    "size": path.stat().st_size,
                }
            # Otherwise already verified against the lock above; leave it alone.
            continue
        try:
            hub.download_file(artifact.repo_id, filename, revision, path)
        except Exception as exc:
            raise AcquisitionError(
                f"{artifact.name} ({artifact.repo_id}): download of "
                f"{filename} at {revision} failed: {exc}"
            ) from exc
        if not path.exists():
            raise AcquisitionError(
                f"{artifact.name} ({artifact.repo_id}): download of "
                f"{filename} produced no file at {path}"
            )
        files_entry[filename] = {
            "sha256": sha256_of(path),
            "size": path.stat().st_size,
        }

    # Drop lock entries for files no longer declared in sources.toml.
    files_entry = {f: files_entry[f] for f in artifact.files if f in files_entry}

    lock["artifacts"][artifact.name] = {
        "repo_id": artifact.repo_id,
        "kind": artifact.kind,
        "target": artifact.target,
        "revision": revision,
        "files": files_entry,
    }
    return Result(artifact, DOWNLOADED)


def acquire(
    artifacts: list[Artifact],
    root: Path,
    lock: dict,
    hub,
) -> list[Result]:
    results: list[Result] = []
    for artifact in artifacts:
        try:
            results.append(acquire_one(artifact, root, lock, hub))
        except AcquisitionError as exc:
            results.append(Result(artifact, FAILED, str(exc)))
    return results


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def report(results: list[Result], lock_written: bool, out=sys.stdout) -> None:
    width = max((len(r.artifact.name) for r in results), default=0)
    for result in results:
        print(f"  {result.artifact.name:<{width}}  {result.status}", file=out)
        if result.detail:
            print(f"      {result.detail}", file=out)
    changed = [r for r in results if r.status == DOWNLOADED]
    broken = [r for r in results if not r.ok]
    if broken:
        print(
            f"\n{len(broken)} artifact(s) failed. Nothing was overwritten; "
            "resolve the artifacts named above and re-run.",
            file=out,
        )
    elif changed:
        print(f"\n{len(changed)} artifact(s) acquired.", file=out)
    else:
        print("\nNo changes.", file=out)
    if lock_written:
        print(f"Lock updated: {LOCK_PATH.name}", file=out)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=REPO_ROOT,
        help="repository root the artifact target paths are relative to",
    )
    args = parser.parse_args(argv)

    try:
        artifacts = load_sources()
        lock = load_lock()
    except (AcquisitionError, OSError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return EXIT_FAILED

    results = acquire(artifacts, args.root, lock, HuggingFaceHub())

    # Only artifacts that completed appear with fresh data; the lock keeps
    # whatever it already said about the ones that did not.
    try:
        lock_written = write_lock_if_changed(lock)
    except OSError as exc:
        print(f"error: could not write {LOCK_PATH}: {exc}", file=sys.stderr)
        return EXIT_FAILED
    report(results, lock_written)

    if any(not r.ok for r in results):
        for result in results:
            if not result.ok:
                print(
                    f"error: {result.artifact.name}: {result.detail}",
                    file=sys.stderr,
                )
        return EXIT_FAILED
    return EXIT_OK


if __name__ == "__main__":
    raise SystemExit(main())
