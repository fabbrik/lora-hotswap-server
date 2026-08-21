"""I/O-matrix tests for acquire.py against a faked Hub. No network.

Every test drives `acquire()` with `FakeHub`; the real `HuggingFaceHub` is never
constructed, so a test run cannot reach huggingface.co even accidentally.
"""

from __future__ import annotations

import hashlib
import json

import pytest

import acquire as A


# ---------------------------------------------------------------- fake hub


class FakeHub:
    """In-memory stand-in for the Hub.

    `repos` maps repo_id -> {revision: {filename: bytes}}; `heads` maps
    repo_id -> the revision `main` resolves to.
    """

    def __init__(self, repos: dict, heads: dict):
        self.repos = repos
        self.heads = heads
        self.resolved: list[tuple[str, str | None]] = []
        self.downloaded: list[tuple[str, str, str]] = []

    def resolve_revision(self, repo_id: str, revision: str | None) -> str:
        self.resolved.append((repo_id, revision))
        if repo_id not in self.repos:
            raise RuntimeError(f"404 repo not found: {repo_id}")
        if revision is None:
            return self.heads[repo_id]
        if revision not in self.repos[repo_id]:
            raise RuntimeError(f"404 revision not found: {revision}")
        return revision

    def download_file(self, repo_id, filename, revision, dest):
        self.downloaded.append((repo_id, filename, revision))
        data = self.repos[repo_id][revision][filename]
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_bytes(data)


class ExplodingHub:
    """Any network contact is a test failure."""

    def resolve_revision(self, repo_id, revision):  # pragma: no cover
        raise AssertionError(f"unexpected resolve_revision({repo_id!r})")

    def download_file(self, *a, **k):  # pragma: no cover
        raise AssertionError("unexpected download_file")


# ---------------------------------------------------------------- fixtures

REV_A = "a" * 40
REV_B = "b" * 40

ALICE = A.Artifact(
    name="alice",
    kind="adapter",
    repo_id="acme/alice",
    target="adapters/alice",
    files=("adapter_config.json", "adapter_model.safetensors"),
)
BOB = A.Artifact(
    name="bob",
    kind="adapter",
    repo_id="acme/bob",
    target="adapters/bob",
    files=("adapter_config.json",),
)

ALICE_FILES = {
    "adapter_config.json": b'{"r": 8}',
    "adapter_model.safetensors": b"\x00weights-alice",
}
BOB_FILES = {"adapter_config.json": b'{"r": 16}'}


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


@pytest.fixture
def hub():
    return FakeHub(
        repos={
            "acme/alice": {REV_A: ALICE_FILES},
            "acme/bob": {REV_B: BOB_FILES},
        },
        heads={"acme/alice": REV_A, "acme/bob": REV_B},
    )


@pytest.fixture
def empty_lock():
    return {"version": A.LOCK_VERSION, "artifacts": {}}


def statuses(results):
    return {r.artifact.name: r.status for r in results}


# ---------------------------------------------------------------- cold


def test_cold_acquisition_downloads_all_and_locks(tmp_path, hub, empty_lock):
    results = A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)

    assert statuses(results) == {"alice": A.DOWNLOADED, "bob": A.DOWNLOADED}
    assert (tmp_path / "adapters/alice/adapter_model.safetensors").exists()

    entry = empty_lock["artifacts"]["alice"]
    assert entry["repo_id"] == "acme/alice"
    assert entry["revision"] == REV_A
    assert entry["files"]["adapter_config.json"]["sha256"] == sha(
        ALICE_FILES["adapter_config.json"]
    )
    assert entry["files"]["adapter_config.json"]["size"] == len(
        ALICE_FILES["adapter_config.json"]
    )
    # No revision was pinned, so `main` (None) was resolved.
    assert ("acme/alice", None) in hub.resolved


# ---------------------------------------------------------------- warm


def test_warm_run_is_a_no_op_and_never_touches_the_hub(tmp_path, hub, empty_lock):
    A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)
    lock_before = A.render_lock(empty_lock)

    results = A.acquire([ALICE, BOB], tmp_path, empty_lock, ExplodingHub())

    assert statuses(results) == {"alice": A.UNCHANGED, "bob": A.UNCHANGED}
    assert all(r.ok for r in results)
    assert A.render_lock(empty_lock) == lock_before


def test_warm_run_leaves_the_lock_file_byte_identical(tmp_path, hub, empty_lock):
    A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)
    lock_path = tmp_path / "acquired.lock.json"
    assert A.write_lock_if_changed(empty_lock, lock_path) is True
    before = lock_path.read_bytes()

    A.acquire([ALICE, BOB], tmp_path, empty_lock, ExplodingHub())

    assert A.write_lock_if_changed(empty_lock, lock_path) is False
    assert lock_path.read_bytes() == before


# ---------------------------------------------------------------- pinned


def test_locked_revision_is_fetched_rather_than_head(tmp_path, empty_lock):
    # The hub has moved on: head is REV_B, but the lock pins REV_A.
    hub = FakeHub(
        repos={"acme/alice": {REV_A: ALICE_FILES, REV_B: {"adapter_config.json": b"new"}}},
        heads={"acme/alice": REV_B},
    )
    empty_lock["artifacts"]["alice"] = {
        "repo_id": "acme/alice",
        "kind": "adapter",
        "target": "adapters/alice",
        "revision": REV_A,
        "files": {
            name: {"sha256": sha(data), "size": len(data)}
            for name, data in ALICE_FILES.items()
        },
    }

    results = A.acquire([ALICE], tmp_path, empty_lock, hub)

    assert statuses(results) == {"alice": A.DOWNLOADED}
    assert hub.resolved == [("acme/alice", REV_A)]
    assert {rev for _, _, rev in hub.downloaded} == {REV_A}
    assert (
        tmp_path / "adapters/alice/adapter_config.json"
    ).read_bytes() == ALICE_FILES["adapter_config.json"]
    assert empty_lock["artifacts"]["alice"]["revision"] == REV_A


def test_missing_locked_revision_fails_naming_artifact_and_revision(
    tmp_path, empty_lock
):
    hub = FakeHub(repos={"acme/alice": {REV_B: ALICE_FILES}}, heads={"acme/alice": REV_B})
    empty_lock["artifacts"]["alice"] = {
        "repo_id": "acme/alice",
        "kind": "adapter",
        "target": "adapters/alice",
        "revision": REV_A,
        "files": {},
    }

    results = A.acquire([ALICE], tmp_path, empty_lock, hub)

    assert statuses(results) == {"alice": A.FAILED}
    detail = results[0].detail
    assert "alice" in detail and REV_A in detail
    # The lock entry for the failed artifact is untouched.
    assert empty_lock["artifacts"]["alice"]["revision"] == REV_A


# ---------------------------------------------------------------- corrupted


def test_hash_mismatch_is_reported_and_never_overwritten(tmp_path, hub, empty_lock):
    A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)
    lock_before = A.render_lock(empty_lock)
    corrupt = tmp_path / "adapters/alice/adapter_model.safetensors"
    corrupt.write_bytes(b"truncated")

    results = A.acquire([ALICE, BOB], tmp_path, empty_lock, ExplodingHub())

    by_name = {r.artifact.name: r for r in results}
    assert by_name["alice"].status == A.MODIFIED
    assert by_name["bob"].status == A.UNCHANGED
    detail = by_name["alice"].detail
    assert "adapter_model.safetensors" in detail and "sha256" in detail
    # Not re-downloaded, not repaired, lock not rewritten.
    assert corrupt.read_bytes() == b"truncated"
    assert A.render_lock(empty_lock) == lock_before


def test_a_modified_artifact_makes_the_run_fail(tmp_path, hub, empty_lock):
    A.acquire([ALICE], tmp_path, empty_lock, hub)
    (tmp_path / "adapters/alice/adapter_config.json").write_bytes(b"tampered")

    results = A.acquire([ALICE], tmp_path, empty_lock, ExplodingHub())

    assert not results[0].ok


def test_file_present_but_absent_from_the_lock_is_reported(tmp_path, hub, empty_lock):
    A.acquire([ALICE], tmp_path, empty_lock, hub)
    del empty_lock["artifacts"]["alice"]["files"]["adapter_config.json"]

    results = A.acquire([ALICE], tmp_path, empty_lock, ExplodingHub())

    assert results[0].status == A.MODIFIED
    assert "absent from the lock" in results[0].detail


# ---------------------------------------------------------------- partial


def test_partial_acquisition_downloads_only_what_is_missing(tmp_path, hub, empty_lock):
    A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)
    hub.downloaded.clear()
    hub.resolved.clear()
    (tmp_path / "adapters/bob").mkdir(parents=True, exist_ok=True)
    (tmp_path / "adapters/bob/adapter_config.json").unlink()

    results = A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)

    assert statuses(results) == {"alice": A.UNCHANGED, "bob": A.DOWNLOADED}
    assert [f for _, f, _ in hub.downloaded] == ["adapter_config.json"]
    assert [r for r, _ in hub.resolved] == ["acme/bob"]


def test_one_missing_file_within_an_artifact_is_fetched_alone(tmp_path, hub, empty_lock):
    A.acquire([ALICE], tmp_path, empty_lock, hub)
    hub.downloaded.clear()
    (tmp_path / "adapters/alice/adapter_model.safetensors").unlink()

    results = A.acquire([ALICE], tmp_path, empty_lock, hub)

    assert statuses(results) == {"alice": A.DOWNLOADED}
    assert [f for _, f, _ in hub.downloaded] == ["adapter_model.safetensors"]


def test_preexisting_file_with_no_lock_entry_is_hashed_not_skipped(
    tmp_path, hub, empty_lock
):
    # A file already sits on disk (manually placed, or acquired before the
    # lock existed) and there is no prior lock entry for its artifact at all.
    # Revision resolution still happens (no lock to trust), but the file
    # itself must never be re-downloaded -- and must not be dropped from the
    # written lock either.
    dest = tmp_path / "adapters/bob/adapter_config.json"
    dest.parent.mkdir(parents=True, exist_ok=True)
    dest.write_bytes(BOB_FILES["adapter_config.json"])

    results = A.acquire([BOB], tmp_path, empty_lock, hub)

    assert statuses(results) == {"bob": A.DOWNLOADED}
    assert hub.downloaded == []
    entry = empty_lock["artifacts"]["bob"]
    assert entry["files"]["adapter_config.json"]["sha256"] == sha(
        BOB_FILES["adapter_config.json"]
    )


# ---------------------------------------------------------------- network


def test_unreachable_repo_fails_only_that_artifact(tmp_path, empty_lock):
    hub = FakeHub(repos={"acme/bob": {REV_B: BOB_FILES}}, heads={"acme/bob": REV_B})

    results = A.acquire([ALICE, BOB], tmp_path, empty_lock, hub)

    assert statuses(results) == {"alice": A.FAILED, "bob": A.DOWNLOADED}
    assert "acme/alice" in results[0].detail
    assert "alice" not in empty_lock["artifacts"]
    assert empty_lock["artifacts"]["bob"]["revision"] == REV_B


def test_download_failure_leaves_the_lock_entry_untouched(tmp_path, empty_lock):
    class HalfBrokenHub(FakeHub):
        def download_file(self, repo_id, filename, revision, dest):
            if filename.endswith(".safetensors"):
                raise RuntimeError("connection reset")
            super().download_file(repo_id, filename, revision, dest)

    hub = HalfBrokenHub(
        repos={"acme/alice": {REV_A: ALICE_FILES}}, heads={"acme/alice": REV_A}
    )

    results = A.acquire([ALICE], tmp_path, empty_lock, hub)

    assert results[0].status == A.FAILED
    assert "connection reset" in results[0].detail
    assert empty_lock["artifacts"] == {}


# ---------------------------------------------------------------- sources


def test_sources_declares_exactly_the_five_agreed_artifacts():
    artifacts = A.load_sources()
    assert [a.repo_id for a in artifacts] == [
        "ggml-org/Qwen3-0.6B-GGUF",
        "charent/self_cognition_Alice",
        "charent/self_cognition_Bob",
        "phh/Qwen3-0.6B-TLDR-Lora",
        "lucylq/qwen3_06B_lora_math",
    ]
    base = artifacts[0]
    # Explicit file selection: never a whole-repo snapshot of the GGUF repo.
    assert base.kind == "base-model"
    assert len(base.files) == 1 and base.files[0].endswith(".gguf")
    for adapter in artifacts[1:]:
        assert adapter.kind == "adapter"
        assert set(adapter.files) == {
            "adapter_config.json",
            "adapter_model.safetensors",
        }


def test_committed_lock_matches_sources_and_records_provenance():
    artifacts = A.load_sources()
    lock = A.load_lock()
    assert lock["version"] == A.LOCK_VERSION
    for artifact in artifacts:
        entry = lock["artifacts"][artifact.name]
        assert entry["repo_id"] == artifact.repo_id
        assert entry["target"] == artifact.target
        assert len(entry["revision"]) == 40
        assert set(entry["files"]) == set(artifact.files)
        for meta in entry["files"].values():
            assert len(meta["sha256"]) == 64
            assert meta["size"] > 0


def test_lock_is_rendered_deterministically(empty_lock):
    empty_lock["artifacts"] = {"z": {"revision": REV_A}, "a": {"revision": REV_B}}
    rendered = A.render_lock(empty_lock)
    assert rendered.endswith("\n")
    assert rendered.index('"a"') < rendered.index('"z"')
    assert json.loads(rendered) == empty_lock


# ---------------------------------------------------------------- main() exit codes


@pytest.fixture
def isolated_lock_path(tmp_path, monkeypatch):
    """Redirect every path this test touches into tmp_path.

    `write_lock_if_changed`'s `path` parameter defaults to the module-level
    `LOCK_PATH` bound at *function-definition* time, so patching `A.LOCK_PATH`
    alone does not redirect it -- the real committed lock file would be
    written to instead. Patch the function itself so its default can never
    fire.
    """
    fake_path = tmp_path / "acquired.lock.json"
    monkeypatch.setattr(A, "LOCK_PATH", fake_path)
    real_write = A.write_lock_if_changed
    monkeypatch.setattr(
        A, "write_lock_if_changed", lambda lock, path=fake_path: real_write(lock, path)
    )
    return fake_path


def test_main_exits_zero_when_everything_is_ok(tmp_path, hub, monkeypatch, isolated_lock_path):
    monkeypatch.setattr(A, "load_sources", lambda: [ALICE, BOB])
    monkeypatch.setattr(A, "load_lock", lambda: {"version": A.LOCK_VERSION, "artifacts": {}})
    monkeypatch.setattr(A, "HuggingFaceHub", lambda: hub)

    assert A.main(["--root", str(tmp_path)]) == A.EXIT_OK


def test_main_exits_nonzero_when_an_artifact_fails(tmp_path, monkeypatch, isolated_lock_path):
    broken_hub = FakeHub(repos={}, heads={})
    monkeypatch.setattr(A, "load_sources", lambda: [ALICE])
    monkeypatch.setattr(A, "load_lock", lambda: {"version": A.LOCK_VERSION, "artifacts": {}})
    monkeypatch.setattr(A, "HuggingFaceHub", lambda: broken_hub)

    assert A.main(["--root", str(tmp_path)]) == A.EXIT_FAILED


def test_main_reports_write_failure_cleanly(tmp_path, hub, monkeypatch, capsys):
    monkeypatch.setattr(A, "load_sources", lambda: [ALICE, BOB])
    monkeypatch.setattr(A, "load_lock", lambda: {"version": A.LOCK_VERSION, "artifacts": {}})
    monkeypatch.setattr(A, "HuggingFaceHub", lambda: hub)

    def boom(*a, **k):
        raise OSError("disk full")

    monkeypatch.setattr(A, "write_lock_if_changed", boom)

    assert A.main(["--root", str(tmp_path)]) == A.EXIT_FAILED
    assert "disk full" in capsys.readouterr().err
