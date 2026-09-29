import glob
import os
import re
import sys
from datetime import datetime, timedelta

root = sys.argv[1]
srv_re = re.compile(r'^\[(\d\d:\d\d:\d\d) INF\] SessionReadiness (\S+) to (\S+) generation (\d+): (\S+) \[(.*?)\], after the answer to (\S+) (\S+)\.')
hmi_re = re.compile(r'^(\S+)\t\w+\t(\w+)\t(.*)$')

totals = [0, 0]
for run in sorted(d for d in glob.glob(os.path.join(root, 'real-onboard-*')) if os.path.isdir(d)):
    name = os.path.basename(run)
    srv = []
    for line in open(os.path.join(run, 'logs', 'control-server.out.log'), encoding='utf-8', errors='replace'):
        m = srv_re.match(line)
        if m:
            srv.append(m.groups())
    hmi = []
    for f in glob.glob(os.path.join(run, 'logs', 'onboard-app', '*.log')):
        for line in open(f, encoding='utf-8', errors='replace'):
            m = hmi_re.match(line.rstrip('\n'))
            if m and re.match(r'(收到SessionReadiness|会话状态发布|丢弃SessionReadiness|判恢复入口)', m.group(3)):
                hmi.append((datetime.fromisoformat(m.group(1)), m.group(3)))
    hmi.sort()
    received = {m.group(1) for _, msg in hmi if (m := re.match(r'收到SessionReadiness：messageId=([0-9a-f-]+)', msg))}
    unpaired = [row[1] for row in srv if row[1] not in received]
    totals[0] += len(srv)
    totals[1] += len(srv) - len(unpaired)
    print(f'=== {name}: server 1103 lines {len(srv)}, paired with onboard 收到SessionReadiness {len(srv) - len(unpaired)}, '
          f'unpaired {unpaired}, onboard diagnostic lines {len(hmi)}')
    prev = None
    for (t, rid, agv, gen, rd, reason, atype, aid) in srv:
        if rd == 'RECOVERY_REQUIRED' and prev == 'READY':
            print(f'  server {t} 1103 {rd}[{reason}] gen={gen} on {atype} {aid[:8]} line={rid[:8]}')
            got = [(ts, msg) for ts, msg in hmi if msg.startswith('收到SessionReadiness') and rid in msg]
            if not got:
                print('    onboard: 收到 NOT FOUND')
                prev = rd
                continue
            t0 = got[0][0]
            for ts, msg in hmi:
                if t0 - timedelta(seconds=2) <= ts <= t0 + timedelta(seconds=2):
                    short = re.sub(r'messageId=[0-9a-f-]+，', '', msg)
                    short = re.sub(r'，connected=True', '', short)
                    print(f'    onboard {ts.strftime("%H:%M:%S.%f")[:-3]} {short[:170]}')
        prev = rd
print(f'=== total: server 1103 lines {totals[0]}, paired {totals[1]}, unpaired {totals[0] - totals[1]}')
