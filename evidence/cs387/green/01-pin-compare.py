# Field-by-field comparison of ZeroChangePins on this branch against the integration tip (control-server#387).
import subprocess, sys, re, os
repo = sys.argv[1]; base = sys.argv[2]
pindir = 'tests/ControlServer.Tests/ZeroChangePins'
names = sorted(os.listdir(os.path.join(repo, pindir)))
def sections(text):
    out = {}; cur = None
    for line in text.replace('\r\n', '\n').split('\n'):
        if line.startswith('## '): cur = line[3:]; out[cur] = []
        elif line: out[cur].append(line)
    return out
def fields(row):
    return dict(re.findall(r"(\w+)=('(?:[^']|'')*'|[^|]*)", row))
lines = []; total = {'lease->record': 0, 'orderintent-two-columns-dropped': 0, 'other-diff': 0, 'files': 0}
lines.append(f'# ZeroChangePins: this branch vs {base}')
for name in names:
    old = subprocess.run(['git', '-C', repo, 'show', f'{base}:{pindir}/{name}'], capture_output=True, text=True, encoding='utf-8').stdout
    new = open(os.path.join(repo, pindir, name), encoding='utf-8').read()
    total['files'] += 1
    if not new.lstrip().startswith('## '):
        same = old.splitlines() == new.splitlines()
        if not same: total['other-diff'] += 1
        lines.append(f"{'SAME' if same else 'DIFFERENT'} {name} (not a table dump)"); continue
    o, n = sections(old), sections(new)
    notes = []
    ok = True
    # every section except the two below: identical
    for sec in sorted(set(o) | set(n)):
        if sec in ('VehicleDispatchLeases', 'VehiclePurposeClaimRecords', 'OrderIntents'): continue
        if o.get(sec) != n.get(sec):
            ok = False; total['other-diff'] += 1; notes.append(f'  DIFF in section {sec}')
    # leases -> records
    leases = o.get('VehicleDispatchLeases', []); records = n.get('VehiclePurposeClaimRecords', [])
    if len(leases) != len(records):
        ok = False; total['other-diff'] += 1; notes.append(f'  lease rows {len(leases)} != record rows {len(records)}')
    else:
        lk = sorted((f['VehicleKey'], f['JourneyId'], f['AcquiredAt'], f['ReleasedAt']) for f in map(fields, leases))
        rk = sorted((f['VehicleKey'], f['JourneyId'], f['AcquiredAt'], f['ReleasedAt']) for f in map(fields, records))
        purposes = {fields(r)['Purpose'] for r in records}; ids = {fields(r)['RecordId'] for r in records}
        if lk != rk or not purposes <= {"'TRANSPORT'"} or not ids <= {'<set>'}:
            ok = False; total['other-diff'] += 1; notes.append(f'  lease/record mismatch {lk} vs {rk} {purposes} {ids}')
        else:
            total['lease->record'] += len(leases)
            for r in records:
                f = fields(r); notes.append(f"  lease->record {f['JourneyId']} {f['VehicleKey']} AcquiredAt={f['AcquiredAt']} ReleasedAt={f['ReleasedAt']} ReleaseReason={f['ReleaseReason']}")
    # OrderIntents: old minus the two trailing columns == new
    oi_old = [re.sub(r"\|VehicleOccupancyClaimedAt=[^|]*\|VehicleOccupancyReleasedAt=[^|]*$", '', r) for r in o.get('OrderIntents', [])]
    stripped = sum(1 for r in o.get('OrderIntents', []) if '|VehicleOccupancyClaimedAt=' in r)
    if oi_old != n.get('OrderIntents', []):
        ok = False; total['other-diff'] += 1; notes.append('  OrderIntents differ beyond the two dropped columns')
    else:
        total['orderintent-two-columns-dropped'] += stripped
        for r in o.get('OrderIntents', []):
            m = re.search(r"UpperId='([^']*)'.*\|VehicleOccupancyClaimedAt=([^|]*)\|VehicleOccupancyReleasedAt=([^|]*)$", r)
            if m: notes.append(f'  OrderIntents {m.group(1)}: dropped VehicleOccupancyClaimedAt={m.group(2)} VehicleOccupancyReleasedAt={m.group(3)}')
    lines.append(f"{'SAME-EXCEPT-RETIREMENT' if ok else 'DIFFERENT'} {name}")
    lines.extend(notes)
lines.append('')
lines.append('totals: ' + ', '.join(f'{k}={v}' for k, v in total.items()))
print('\n'.join(lines))
