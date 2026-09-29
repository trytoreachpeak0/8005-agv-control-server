import glob
import os
import re
import sys

root = sys.argv[1]
for run in sorted(d for d in glob.glob(os.path.join(root, 'real-onboard-*')) if os.path.isdir(d)):
    lines = []
    for f in glob.glob(os.path.join(run, 'logs', 'onboard-app', '*.log')):
        for line in open(f, encoding='utf-8', errors='replace'):
            parts = line.rstrip('\n').split('\t')
            if len(parts) >= 4:
                lines.append((parts[0], parts[3]))
    lines.sort()
    # After a RecoveryRequired publication of generation g, no Ready publication of g until a READY line of g is received.
    violations = []
    rr_open = {}
    for ts, msg in lines:
        gm = re.search(r'generation=(\d+)', msg)
        g = gm.group(1) if gm else None
        if msg.startswith('收到SessionReadiness') and 'readiness=READY' in msg:
            rr_open.pop(g, None)
        elif msg.startswith('会话状态发布') and g:
            if 'readiness=RecoveryRequired' in msg:
                rr_open[g] = ts
            elif 'readiness=Ready' in msg and g in rr_open:
                violations.append((ts, msg[:120]))
    dropped = [(ts, msg[:160]) for ts, msg in lines if msg.startswith('丢弃SessionReadiness')]
    srv = open(os.path.join(run, 'logs', 'control-server.out.log'), encoding='utf-8', errors='replace').read()
    ids = re.findall(r'\] SessionReadiness ([0-9a-f-]{36}) to ', srv)
    held = len(re.findall(r'held back for the recovery', srv))
    onboard = '\n'.join(m for _, m in lines)
    missing = [i for i in ids if f'messageId={i}' not in onboard]
    print(f'{os.path.basename(run)}: Ready-after-RR={len(violations)} dropped={len(dropped)} 1103={len(ids)} missing={len(missing)} 1104={held}')
    for v in violations:
        print('   VIOLATION', v)
    for d in dropped:
        print('   DROPPED', d)
    for i in missing:
        print('   MISSING', i)
