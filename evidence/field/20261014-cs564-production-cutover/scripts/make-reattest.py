import json, hashlib, os, sys
base = 'C:/Users/szy/Desktop/8005-workspace-v2/evidence/field/20261014-req0263-slot-verify'
src = {'agv01': 'agv01-run1/01-agv01.json', 'agv02': 'run3/02-agv02.json', 'agv03': 'agv03-run1/03-agv03.json'}
new_model = '74da3fee0e164c3e89b161c306ba5edb'
approved_at = sys.argv[1]
for car, rel in src.items():
    p = os.path.join(base, rel)
    raw = open(p, 'rb').read()
    sha = hashlib.sha256(raw).hexdigest().upper()
    r = json.loads(raw.decode('utf-8'))
    old_model = r['slotModelVersionId']
    srcdir = rel.split('/')[0]
    r['slotModelVersionId'] = new_model
    for s in r['slots']:
        s['fieldRecordReference'] = f"../{srcdir}/{s['fieldRecordReference']}"
        s['note'] = (f"沿用 2026-10-14 逐仓核对（原记录 {rel}，SHA-256 {sha}，车型 {old_model}）；"
                     f"库 C 重新 seed 后车型 id 变为 {new_model}，硬件未动。用户 Zhengyu Shao 于 {approved_at} 在对话中批准跳过重核。原注：{s['note']}")
    out_dir = os.path.join(base, f'{car}-dbC-reattest')
    os.makedirs(out_dir, exist_ok=False)
    out = os.path.join(out_dir, os.path.basename(rel))
    open(out, 'w', encoding='utf-8').write(json.dumps(r, ensure_ascii=False, indent=2))
    print(car, out, 'from', sha[:16], old_model, '->', new_model, 'slots', len(r['slots']), all(s['openSignalConfirmed'] and s['closeSignalConfirmed'] and s['inPlaceSignalConfirmed'] for s in r['slots']))
