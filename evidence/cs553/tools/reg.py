import sys,bisect,importlib.util,statistics as st
spec=importlib.util.spec_from_file_location('m',sys.argv[2]+'/trxlib.py'); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
res=m.load(sys.argv[1])
ev=sorted([(r['s'],1) for r in res]+[(r['e'],-1) for r in res]); times=[e[0] for e in ev]; cum=[];c=0
for _,d in ev: c+=d; cum.append(c)
def ac(r):
    n=20;t=0
    for k in range(n):
        x=r['s']+(r['e']-r['s'])*(k+.5)/n; i=bisect.bisect_right(times,x)-1; t+=cum[i]
    return t/n
cls=sys.argv[3].split(',')
xs=[];ys=[]
for r in res:
    if r['cls'] in cls and r['d']>0.3: xs.append(ac(r)); ys.append(r['d'])
mx,my=st.mean(xs),st.mean(ys)
b=sum((x-mx)*(y-my) for x,y in zip(xs,ys))/sum((x-mx)**2 for x in xs); a=my-b*mx
r2=1-sum((y-a-b*x)**2 for x,y in zip(xs,ys))/sum((y-my)**2 for y in ys)
print('n=%d slope=%.2fs/concurrent test intercept=%.2fs R2=%.2f'%(len(xs),b,a,r2))
long=[r for r in res if r['d']>3]
print('tests>3s:',len(long),'sum',round(sum(r['d'] for r in long)),'; tests<=3s:',len(res)-len(long),'sum',round(sum(r['d'] for r in res if r['d']<=3)))
