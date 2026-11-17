"""cs#896: compare each WirePin baseline with its re-recorded .actual.

The only change allowed is the protocol 7.0.0 worklist item field ``eqp``: every item of every
CurrentStopWorklistSnapshot payload gains exactly one key ``eqp``, and nothing else in any payload,
header, wire line or RIoT order moves -- except the payloadSha256 of the lines whose payload gained it.

Usage: python compare-wirepins.py <old-dir> <new-dir> [pin ...]
Prints one PASS/FAIL line per pin and exits 1 if any pin fails.
"""
import json
import re
import sys
from pathlib import Path

SHA = re.compile(r"payloadSha256=([0-9a-f]{64})")
MID = re.compile(r"messageId=(\S+)")


def strip_eqp(payload):
    items = payload.get("items")
    added = 0
    values = []
    if isinstance(items, list):
        for item in items:
            if "eqp" in item:
                values.append(item.pop("eqp"))
                added += 1
    return payload, added, values


def compare(old_text, new_text):
    problems = []
    old_lines, new_lines = old_text.split("\n"), new_text.split("\n")
    if len(old_lines) != len(new_lines):
        return [f"line count {len(old_lines)} -> {len(new_lines)}"], 0, set()
    section = None
    changed_sha = {}  # messageId -> (old sha, new sha) from the outbound section
    last_header = None
    eqp_added = 0
    eqp_values = set()
    for number, (old, new) in enumerate(zip(old_lines, new_lines), start=1):
        if old.startswith("## "):
            if old != new:
                problems.append(f"{number}: section header differs")
            section = old
            continue
        if section and section.startswith("## Outbound") and old.startswith("   payload="):
            old_payload = json.loads(old[len("   payload="):])
            new_payload = json.loads(new[len("   payload="):])
            stripped, added, values = strip_eqp(new_payload)
            _, old_added, _ = strip_eqp(old_payload)
            if old_added:
                problems.append(f"{number}: old baseline already carries eqp")
            if stripped != old_payload:
                problems.append(f"{number}: payload differs beyond eqp")
            is_worklist = "type=CurrentStopWorklistSnapshot " in last_header
            item_count = len(old_payload.get("items", [])) if is_worklist else 0
            if added != item_count:
                problems.append(f"{number}: {added} eqp keys added, {item_count} items expected")
            eqp_added += added
            eqp_values.update(json.dumps(value, ensure_ascii=False) for value in values)
            mid = MID.search(last_header).group(1)
            old_sha_line, new_sha_line = last_header_pair
            o, n = SHA.search(old_sha_line).group(1), SHA.search(new_sha_line).group(1)
            if (o != n) != (added > 0):
                problems.append(f"{number}: digest changed={o != n} but eqp added={added}")
            changed_sha[mid] = (o, n)
            continue
        if section and section.startswith("## Outbound"):
            last_header = old
            last_header_pair = (old, new)
            if SHA.sub("", old) != SHA.sub("", new):
                problems.append(f"{number}: outbound header differs beyond the digest")
            continue
        if section and section.startswith("## Wire"):
            if SHA.sub("", old) != SHA.sub("", new):
                problems.append(f"{number}: wire line differs beyond the digest")
            o, n = SHA.search(old).group(1), SHA.search(new).group(1)
            mid = MID.search(old).group(1)
            if o != n:
                expected = changed_sha.get(mid)
                if expected is None or expected[1] != n or expected[0] != o:
                    # A replayed line may carry a payload the outbox row no longer holds; it must still be
                    # explained by some outbound payload change of the same message.
                    problems.append(f"{number}: wire digest change for {mid} not explained by the outbound section")
            continue
        if old != new:
            problems.append(f"{number}: differs ({section})")
    return problems, eqp_added, eqp_values


def main():
    old_dir, new_dir = Path(sys.argv[1]), Path(sys.argv[2])
    pins = sys.argv[3:] or sorted(path.stem for path in old_dir.glob("*.txt"))
    failed = False
    for pin in pins:
        old_text = (old_dir / f"{pin}.txt").read_text(encoding="utf-8").replace("\r\n", "\n")
        new_path = new_dir / f"{pin}.txt"
        if not new_path.exists():
            new_path = new_dir / f"{pin}.txt.actual"
        new_text = new_path.read_text(encoding="utf-8").replace("\r\n", "\n")
        problems, added, values = compare(old_text, new_text)
        if old_text == new_text:
            print(f"PASS {pin}: identical (no worklist items)")
        elif problems:
            failed = True
            print(f"FAIL {pin}: " + "; ".join(problems[:5]))
        else:
            print(f"PASS {pin}: {added} eqp keys added, values {sorted(values)}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
