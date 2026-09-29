"""Mechanical comparison of the re-recorded zero-change pins with the integration branch's (control-server#399).

Old pins: the files on fp/v2-impl (identical to this branch's before the re-record). New pins: this branch's working tree.
Judgement: in every new pin, delete exactly the fields batch 9 appended, and the result must equal the old pin byte for
byte. The expected number of each field is computed from the OLD pin first (rows of the table that gets the column),
and then compared with what the new pin actually holds.
"""
import os
import subprocess
import sys

REPO = sys.argv[1]
BASE = sys.argv[2]  # the integration branch commit the old pins are read from
PIN_DIR = 'tests/ControlServer.Tests/ZeroChangePins'

# table section -> the exact field text batch 9 appends to each of its rows
APPENDED = {
    'OrderIntents': ["|OrderShape='SINGLE_MOVE'"],
    'JourneyRuntimes': ['|ChargingPolicyVersion=NULL', '|PublishedBatteryState=NULL'],
}


def sections(text):
    current, out = None, []
    for line in text.split('\n'):
        if line.startswith('## '):
            current = line[3:].split(' ')[0]
        out.append((current, line))
    return out


def old_text(name):
    return subprocess.run(['git', '-C', REPO, 'show', f'{BASE}:{PIN_DIR}/{name}'], check=True,
                          capture_output=True).stdout.decode('utf-8').replace('\r\n', '\n')


out = []
failures = 0
names = sorted(os.listdir(os.path.join(REPO, PIN_DIR)))
out.append(f'base (old pins): {BASE}')
out.append(f'pins: {len(names)}')
totals = {field: [0, 0] for fields in APPENDED.values() for field in fields}
for name in names:
    old = old_text(name)
    new = open(os.path.join(REPO, PIN_DIR, name), encoding='utf-8', newline='').read().replace('\r\n', '\n')
    old_lines, new_lines = sections(old), sections(new)
    verdict = []
    if len(old_lines) != len(new_lines):
        verdict.append(f'LINE COUNT {len(old_lines)} -> {len(new_lines)}')
    stripped_new = []
    for table, line in new_lines:
        for field in APPENDED.get(table, []):
            if line.startswith('## '):
                continue
            n = line.count(field)
            totals[field][1] += n
            if n != 1:
                verdict.append(f'{table}: {field} appears {n} times on one row')
            line = line.replace(field, '', 1)
        # a batch 9 field anywhere outside its own table is a difference of another kind
        for other, fields in APPENDED.items():
            if other != table:
                for field in fields:
                    if field in line:
                        verdict.append(f'{field} outside {other}')
        stripped_new.append(line)
    for table, line in old_lines:
        if not line.startswith('## '):
            for field in APPENDED.get(table, []):
                totals[field][0] += 1
    same = '\n'.join(stripped_new) == old
    if not same:
        verdict.append('NOT byte-identical after removing the appended fields')
    changed = old != new
    failures += bool(verdict)
    out.append(f"{name}: {'changed' if changed else 'unchanged'}, "
               f"{'identical after removal' if same else 'DIFFERENT'}{'; ' + '; '.join(verdict) if verdict else ''}")
out.append('expected (rows in the old pins) vs actual (fields in the new pins):')
for field, (expected, actual) in totals.items():
    out.append(f'  {field}: expected {expected}, actual {actual}{"" if expected == actual else "  MISMATCH"}')
    failures += expected != actual
out.append('RESULT: ' + ('PASS' if failures == 0 else f'FAIL ({failures})'))
print('\n'.join(out))
sys.exit(1 if failures else 0)
