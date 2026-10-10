import sys,importlib.util,collections
spec=importlib.util.spec_from_file_location('m',sys.argv[3]+'/trxlib.py'); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
def summ(p):
    res=m.load(p); t0=min(r['s'] for r in res); t1=max(r['e'] for r in res)
    # A multiset, not a dict: a test name can appear more than once in a trx (repeated theory rows), and every copy is compared.
    return res,collections.Counter((r['full'],r['name'],r['out']) for r in res),(t1-t0).total_seconds()
a,A,wa=summ(sys.argv[1]); b,B,wb=summ(sys.argv[2])
def outcomes(c): return dict(collections.Counter(k[2] for k in c.elements()))
print('before: entries %d outcomes %s wall %.0f s sumdur %.0f s'%(len(a),outcomes(A),wa,sum(r['d'] for r in a)))
print('after:  entries %d outcomes %s wall %.0f s sumdur %.0f s'%(len(b),outcomes(B),wb,sum(r['d'] for r in b)))
names_a=collections.Counter((r['full'],r['name']) for r in a); names_b=collections.Counter((r['full'],r['name']) for r in b)
print('names appearing more than once (before/after):',[(k[1],names_a[k],names_b[k]) for k in sorted(set(names_a)|set(names_b)) if names_a[k]>1 or names_b[k]>1])
only_a=sorted((A-B).elements()); only_b=sorted((B-A).elements())
print('(name, outcome) entries only in before (%d):'%len(only_a)); [print('  ',k[1],k[2]) for k in only_a]
print('(name, outcome) entries only in after (%d):'%len(only_b)); [print('  ',k[1],k[2]) for k in only_b]
for cls in ['IdleReturnExecutionTests','VehicleFaultIsolationTests','EmergencyStopSupervisorTests','MultiVehicleExecutionTests']:
    for lab,res in [('before',a),('after',b)]:
        L=[r for r in res if r['cls']==cls]
        print('%-30s %-6s n=%d sum=%.0f s span=%.0f s'%(cls,lab,len(L),sum(r['d'] for r in L),(max(r['e'] for r in L)-min(r['s'] for r in L)).total_seconds()))
rows=collections.defaultdict(list)
for r in b: rows[r['cls']].append(r)
top=sorted(rows.items(),key=lambda kv:-(max(r['e'] for r in kv[1])-min(r['s'] for r in kv[1])).total_seconds())[:10]
print('after: longest classes by span'); [print('   %-48s n=%d span=%.0f s'%(c,len(L),(max(r['e'] for r in L)-min(r['s'] for r in L)).total_seconds())) for c,L in top]
