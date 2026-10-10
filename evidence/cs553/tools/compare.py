import sys,importlib.util,collections
spec=importlib.util.spec_from_file_location('m',sys.argv[3]+'/trxlib.py'); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
def summ(p):
    res=m.load(p); t0=min(r['s'] for r in res); t1=max(r['e'] for r in res)
    return res,{(r['full'],r['name']):r['out'] for r in res},(t1-t0).total_seconds()
a,A,wa=summ(sys.argv[1]); b,B,wb=summ(sys.argv[2])
print('before: tests %d outcomes %s wall %.0f s sumdur %.0f s'%(len(a),dict(collections.Counter(A.values())),wa,sum(r['d'] for r in a)))
print('after:  tests %d outcomes %s wall %.0f s sumdur %.0f s'%(len(b),dict(collections.Counter(B.values())),wb,sum(r['d'] for r in b)))
print('duplicate names before/after:',len(a)-len(A),len(b)-len(B))
only_a=sorted(set(A)-set(B)); only_b=sorted(set(B)-set(A))
print('only in before (%d):'%len(only_a)); [print('  ',k[1]) for k in only_a]
print('only in after (%d):'%len(only_b)); [print('  ',k[1]) for k in only_b]
diff=[(k,A[k],B[k]) for k in set(A)&set(B) if A[k]!=B[k]]
print('outcome differs on common tests (%d):'%len(diff)); [print('  ',k[1],x,'->',y) for k,x,y in sorted(diff)]
for cls in ['IdleReturnExecutionTests','VehicleFaultIsolationTests','EmergencyStopSupervisorTests','MultiVehicleExecutionTests']:
    for lab,res in [('before',a),('after',b)]:
        L=[r for r in res if r['cls']==cls]
        print('%-30s %-6s n=%d sum=%.0f s span=%.0f s'%(cls,lab,len(L),sum(r['d'] for r in L),(max(r['e'] for r in L)-min(r['s'] for r in L)).total_seconds()))
rows=collections.defaultdict(list)
for r in b: rows[r['cls']].append(r)
top=sorted(rows.items(),key=lambda kv:-(max(r['e'] for r in kv[1])-min(r['s'] for r in kv[1])).total_seconds())[:10]
print('after: longest classes by span'); [print('   %-48s n=%d span=%.0f s'%(c,len(L),(max(r['e'] for r in L)-min(r['s'] for r in L)).total_seconds())) for c,L in top]
