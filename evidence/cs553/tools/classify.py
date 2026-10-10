import sys,re,os,glob,collections,importlib.util
spec=importlib.util.spec_from_file_location('m',sys.argv[2]+'/trxlib.py'); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
res=m.load(sys.argv[1]); root=sys.argv[3]
files={p:open(p,encoding='utf-8-sig').read() for p in glob.glob(root+'/**/*.cs',recursive=True)}
mig=[p for p,t in files.items() if 'MigrateAsync' in t or 'EnsureCreated' in t]
# types declared in migrating files
decl=set()
for p in mig:
    for mm in re.finditer(r'\b(?:class|record)\s+(\w+)',files[p]): decl.add(mm.group(1))
def clsfiles(c): return [p for p,t in files.items() if re.search(r'\b(?:partial\s+)?class\s+'+c+r'\b',t)]
kind={}
for c in set(r['cls'] for r in res):
    fs=clsfiles(c); txt=''.join(files[p] for p in fs)
    if 'MigrateAsync' in txt: k='migrate-direct'
    elif 'EnsureCreated' in txt: k='ensurecreated-direct'
    elif any(re.search(r'\b'+d+r'\b',txt) for d in decl if len(d)>5): k='via-harness'
    else: k='none'
    kind[c]=k
agg=collections.defaultdict(lambda:[0,0,0,0.0])
for r in res:
    a=agg[kind[r['cls']]]; a[0]+=1; a[1]+= r['d']>3; a[3]+=r['d']
for k,v in agg.items(): print('%-22s tests=%5d  >3s=%5d  sumdur=%7.0f'%(k,v[0],v[1],v[3]))
slow_none=collections.Counter(r['cls'] for r in res if r['d']>3 and kind[r['cls']]=='none')
print('slow tests in classes with no db fixture:',slow_none.most_common(15))
