"""Ticket 11 publication read-back evidence.

Every verdict here is a read-back of what GitHub actually serves, never an
upload return code. Each detector is exercised in both directions: the GREEN
case is the published artefact, the RED case is a single-character mutation of
the expectation the same comparator is fed.

Usage:
    gh release download w2g-mvp-rc-0.2.0 --dir <downloads> --clobber
    python Verify-Publication.py <downloads> <local-notes-dir> [--repo-dir <path>]

<local-notes-dir> holds the local copies the release bodies were published
from: rc020-body.md and rc011-body-updated.md.
"""
import argparse
import hashlib
import json
import pathlib
import subprocess
import sys
import zipfile

TOP = "w2g-rc-20260901b-19ce7db/"
TAG = "w2g-mvp-rc-0.2.0"
CANDIDATE_COMMIT = "19ce7db70893afea6c6361988c3bc612d77569d0"

# Recorded before upload; also the three rows in section 2 of the release notes.
PUBLISHED = {
    "w2g-rc-20260901b-19ce7db.zip": ("abbff6e0d9afe415080b774395695bff4dad9f6437a9455f3e3c6d84aa1ceca9", 123991314),
    "release-manifest.json": ("2389853936f9453c2fa93168c6d1316f0e83ba0367246801be98653aa54df3d9", 171966),
    "SHA256SUMS.txt": ("5e3aeb3ab94a29723b9c7e2c79b5bbc2548777a78f181295368363e7e7cdbd02", 99003),
}

results = []


def record(check, verdict, detail=""):
    results.append((check, verdict, detail))
    print(f"{verdict:10} {check}" + (f"  -- {detail}" if detail else ""))


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def flip_last(text):
    return text[:-1] + ("0" if text[-1] != "0" else "1")


def normalise(path):
    """Line endings only. The body itself is compared character for character."""
    return path.read_text(encoding="utf-8").replace("\r\n", "\n").replace("\r", "\n").rstrip("\n")


def gh_body(tag, out_path):
    """gh --jq output is split into an array by PowerShell, so land it on disk first."""
    body = subprocess.run(
        ["gh", "release", "view", tag, "--json", "body", "-q", ".body"],
        capture_output=True, text=True, encoding="utf-8", check=True,
    ).stdout
    out_path.write_text(body, encoding="utf-8")
    return out_path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("downloads", type=pathlib.Path)
    parser.add_argument("notes", type=pathlib.Path)
    parser.add_argument("--repo-dir", type=pathlib.Path, default=pathlib.Path("."))
    args = parser.parse_args()
    dl, notes = args.downloads, args.notes

    print("== 1. remote assets re-hashed after download ==")
    for name, (expected, size) in PUBLISHED.items():
        path = dl / name
        if not path.exists():
            record(f"ASSET-{name}", "MISSING")
            continue
        actual, actual_size = sha256_file(path), path.stat().st_size
        ok = actual == expected and actual_size == size
        record(f"ASSET-{name}", "MATCH" if ok else "MISMATCH", f"{actual} / {actual_size} B")
        # RED: the same comparator against a one-character mutation of the expectation.
        record(f"ASSET-{name}-RED", "MISMATCH" if actual != flip_last(expected) else "MATCH",
               "one-character mutation of the expected digest")

    print("\n== 2. the downloaded zip is internally consistent ==")
    zip_path = dl / "w2g-rc-20260901b-19ce7db.zip"
    with zipfile.ZipFile(zip_path) as archive:
        names = archive.namelist()
        tops = {n.split("/")[0] for n in names}
        record("ZIP-SINGLE-TOPLEVEL", "PASS" if len(tops) == 1 else "FAIL", f"{len(names)} entries, top={tops}")
        entries = set(names)
        ok = mismatch = missing = 0
        for line in archive.read(TOP + "SHA256SUMS.txt").decode("utf-8").splitlines():
            if "  " not in line:
                continue
            expected, rel = line.split("  ", 1)
            entry = TOP + rel
            if entry not in entries:
                missing += 1
                continue
            digest = hashlib.sha256()
            with archive.open(entry) as handle:
                for chunk in iter(lambda: handle.read(1 << 20), b""):
                    digest.update(chunk)
            if digest.hexdigest() == expected:
                ok += 1
            else:
                mismatch += 1
        record("ZIP-HASHES", "PASS" if mismatch == 0 and missing == 0 else "FAIL",
               f"ok={ok} mismatch={mismatch} missing={missing}")
        # The notes claim the two standalone assets are the same files as the ones in the zip.
        for name in ("release-manifest.json", "SHA256SUMS.txt"):
            same = sha256_file(dl / name) == hashlib.sha256(archive.read(TOP + name)).hexdigest()
            record(f"ZIP-SAME-AS-ASSET-{name}", "SAME" if same else "DIFFERENT")

    print("\n== 3. the tag peels to the commit the candidate manifest records ==")
    subprocess.run(["git", "fetch", "origin", "--force", f"refs/tags/{TAG}:refs/tags/verify-020"],
                   cwd=args.repo_dir, capture_output=True, check=True)
    tag_type = subprocess.run(["git", "cat-file", "-t", "refs/tags/verify-020"],
                              cwd=args.repo_dir, capture_output=True, text=True, check=True).stdout.strip()
    peeled = subprocess.run(["git", "rev-parse", "refs/tags/verify-020^{commit}"],
                            cwd=args.repo_dir, capture_output=True, text=True, check=True).stdout.strip()
    manifest_commit = json.loads((dl / "release-manifest.json").read_text(encoding="utf-8"))["components"]["controlServer"]["commit"]
    record("TAG-IS-ANNOTATED", "PASS" if tag_type == "tag" else "FAIL", f"type={tag_type}")
    record("TAG-PEELS-TO-MANIFEST", "MATCH" if peeled == manifest_commit == CANDIDATE_COMMIT else "MISMATCH", peeled)
    record("TAG-PEELS-TO-MANIFEST-RED", "MISMATCH" if peeled != flip_last(manifest_commit) else "MATCH",
           "one-character mutation of the manifest commit")

    print("\n== 4. the live bodies are verbatim the local copies ==")
    for label, local_name, tag in (("0.2.0", "rc020-body.md", "w2g-mvp-rc-0.2.0"),
                                   ("0.1.1", "rc011-body-updated.md", "w2g-mvp-rc-0.1.1")):
        live = gh_body(tag, dl / f"live-{label}.md")
        local_text, live_text = normalise(notes / local_name), normalise(live)
        record(f"BODY-{label}", "MATCH" if local_text == live_text else "MISMATCH",
               f"{len(local_text)} vs {len(live_text)} chars")
    # RED: one character flipped in the local copy must break the same comparator.
    body = normalise(notes / "rc020-body.md")
    tampered = body[:100] + ("X" if body[100] != "X" else "Y") + body[101:]
    record("BODY-0.2.0-RED", "MISMATCH" if tampered != normalise(dl / "live-0.2.0.md") else "MATCH",
           "one character flipped in the local copy")

    bad = [r for r in results if r[1] in ("FAIL", "MISSING", "DIFFERENT")]
    bad += [r for r in results if r[0].endswith("-RED") and r[1] == "MATCH"]
    bad += [r for r in results if not r[0].endswith("-RED") and r[1] == "MISMATCH"]
    print(f"\n== {len(results) - len(bad)} / {len(results)} verdicts as expected ==")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
