import sys,collections,statistics as st
sys.path.insert(0,sys.argv[2])
import importlib.util
spec=importlib.util.spec_from_file_location('trxmod',sys.argv[2]+'/trxlib.py'); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
res=m.load(sys.argv[1]); t0=min(r['s'] for r in res)
ev=sorted([(r['s'],1) for r in res]+[(r['e'],-1) for r in res])
import bisect
times=[e[0] for e in ev]; cum=[];c=0
for _,d in ev: c+=d; cum.append(c)
def avgconc(r):
    # average concurrency over the test's lifetime, sampled 20 points
    n=20; tot=0
    for k in range(n):
        t=r['s']+(r['e']-r['s'])*(k+0.5)/n
        i=bisect.bisect_right(times,t)-1; tot+=cum[i] if i>=0 else 0
    return tot/n
for cls in sys.argv[3].split(','):
    L=[r for r in res if r['cls']==cls]
    b=collections.defaultdict(list)
    for r in L: b[min(int(avgconc(r)//4)*4,20)].append(r['d'])
    print(cls, {k:(len(v),round(st.median(v),1)) for k,v in sorted(b.items())})
