"""control-server#382: prove each re-recorded WirePin differs from its batch-p3/v3 baseline only by the two
keys protocol 3.0.0 made required -- stopEndedReason on CurrentStopWorklistSnapshot, checkPurpose on
PreDepartureSafetyCheck. Usage: python wirepin-diff.py <baseline-dir> <actual-dir>  (actual files named *.txt.actual
or *.txt). Exit 1 and a FAIL line on any difference that is not explained."""
import json, re, sys, os

NEW = {'CurrentStopWorklistSnapshot': 'stopEndedReason', 'PreDepartureSafetyCheck': 'checkPurpose'}

# How each pinned journey ends, read off the test that records it rather than off the new pin, and mapped by the
# table in 8005-agv-program PR #163 item 3: a worklist with items must say null, an empty one must say this.
# None means the pin has no empty worklist at all, so an empty one there is itself a failure.
ENDING = {
    'cancellation-before-sublot.txt': 'LOAD_CANCELLED',        # cancelled before any sublot entry
    'departure-check-expired.txt': None,                       # stops at the reissued departure check
    'determinate-load-failure.txt': 'STATION_DEADLINE_EXPIRED',  # determinate load failure -> CANCELLED_BY_STATION_TIMEOUT
    'in-flight-cancellation.txt': 'LOAD_CANCELLED',            # cancelled after the load was commanded
    'normal-journey.txt': 'COMPLETED',                         # unloaded normally
    'reconnect-replay.txt': None,                              # stops mid-journey after the reconnect
    'station-deadline.txt': 'STATION_DEADLINE_EXPIRED',        # station departure deadline expired
    'two-journeys-one-vehicle.txt': 'COMPLETED',               # two normal journeys
}
PURPOSE = 'DEPARTURE'  # the only check purpose the server assembles today
base_dir, actual_dir = sys.argv[1], sys.argv[2]
failed = False
for name in sorted(f for f in os.listdir(base_dir) if f.endswith('.txt')):
    act_path = os.path.join(actual_dir, name + '.actual')
    if not os.path.exists(act_path):
        act_path = os.path.join(actual_dir, name)
    E = open(os.path.join(base_dir, name), encoding='utf-8').read().splitlines()
    A = open(act_path, encoding='utf-8').read().splitlines()
    problems = []
    if len(E) != len(A):
        problems.append(f'line count {len(E)} != {len(A)}')
    # Expected counts, computed from the baseline alone: one new key per OUTBOX row of each type. Outbox rows
    # carry ' t=' and are followed by their payload; the Wire section repeats the same messages as digests only,
    # so counting it too (the first version of this script did) triples the expectation for nothing.
    expected = {key: sum(1 for l in E if l[:1].isdigit() and ' t=' in l and f' type={t} ' in l)
                for t, key in NEW.items()}
    counted = {key: 0 for key in NEW.values()}
    values = {key: set() for key in NEW.values()}
    shamap = {}
    if not problems:
        for i, (e, a) in enumerate(zip(E, A)):
            if e.strip().startswith('payload='):
                pe, pa = json.loads(e.strip()[8:]), json.loads(a.strip()[8:])
                mtype = re.search(r' type=(\S+) ', E[i - 1]).group(1)
                allowed = NEW.get(mtype)
                extra = set(pa) - set(pe)
                if extra - ({allowed} if allowed else set()):
                    problems.append(f'line {i + 1}: unexpected keys {sorted(extra)} on {mtype}')
                if allowed and allowed in pa:
                    counted[allowed] += 1
                    values[allowed].add(json.dumps(pa[allowed]))
                    if allowed == 'stopEndedReason':
                        want = None if pa.get('items') else ENDING.get(name, '<no table entry>')
                        if pa[allowed] != want:
                            problems.append(f'line {i + 1}: stopEndedReason {pa[allowed]!r}, expected {want!r} '
                                            f'({"items present" if pa.get("items") else "empty worklist"})')
                    elif pa[allowed] != PURPOSE:
                        problems.append(f'line {i + 1}: checkPurpose {pa[allowed]!r}, expected {PURPOSE!r}')
                if {k: v for k, v in pa.items() if k != allowed} != pe:
                    problems.append(f'line {i + 1}: payload differs beyond the new key')
                he = re.search(r'payloadSha256=(\w+)', E[i - 1]).group(1)
                ha = re.search(r'payloadSha256=(\w+)', A[i - 1]).group(1)
                if shamap.setdefault(he, ha) != ha:
                    problems.append(f'line {i + 1}: one baseline digest maps to two actual digests')
        for i, (e, a) in enumerate(zip(E, A)):
            if e.strip().startswith('payload='):
                continue
            if re.sub(r'payloadSha256=\w+', '', e) != re.sub(r'payloadSha256=\w+', '', a):
                problems.append(f'line {i + 1}: header differs: {e!r} -> {a!r}')
                continue
            me = re.search(r'payloadSha256=(\w+)', e)
            if me:
                ha = re.search(r'payloadSha256=(\w+)', a).group(1)
                if me.group(1) != ha and shamap.get(me.group(1)) != ha:
                    problems.append(f'line {i + 1}: digest change not explained by an outbox payload')
    if counted != expected:
        problems.append(f'new-key count {counted} != expected {expected}')
    verdict = 'FAIL' if problems else 'PASS'
    failed |= bool(problems)
    print(f'{verdict} {name} expected={expected} counted={counted} values={ {k: sorted(v) for k, v in values.items() if v} }')
    for p in problems[:5]:
        print('   ', p)
sys.exit(1 if failed else 0)
