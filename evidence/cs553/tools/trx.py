import sys, xml.etree.ElementTree as ET, collections, datetime as dt, statistics as st
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def ts(s):
    # 2026-10-10T09:31:00.1234567+08:00
    if '.' in s:
        a,b=s.split('.',1); frac=''; i=0
        while i<len(b) and b[i].isdigit(): frac+=b[i]; i+=1
        s=a+'.'+frac[:6]+b[i:]
    return dt.datetime.fromisoformat(s)
def dur(s):
    h,m,x=s.split(':'); return int(h)*3600+int(m)*60+float(x)
def load(p):
    r=ET.parse(p).getroot()
    defs={}
    for u in r.iterfind('.//t:UnitTest',ns):
        tm=u.find('t:TestMethod',ns); defs[u.get('id')]=(tm.get('className'),tm.get('name'))
    res=[]
    for x in r.iterfind('.//t:UnitTestResult',ns):
        c,n=defs[x.get('testId')]
        res.append(dict(cls=c.split('.')[-1],full=c,name=x.get('testName'),out=x.get('outcome'),
            d=dur(x.get('duration','0:0:0')),s=ts(x.get('startTime')),e=ts(x.get('endTime'))))
    return res
res=load(sys.argv[1]); top=int(sys.argv[2]) if len(sys.argv)>2 else 25
t0=min(r['s'] for r in res); t1=max(r['e'] for r in res)
print('tests',len(res),'outcomes',collections.Counter(r['out'] for r in res))
print('wall %.0f s  sumdur %.0f s  avg concurrency %.1f'%((t1-t0).total_seconds(),sum(r['d'] for r in res),sum(r['d'] for r in res)/(t1-t0).total_seconds()))
by=collections.defaultdict(list)
for r in res: by[r['cls']].append(r)
rows=[]
for c,L in by.items():
    s=min(r['s'] for r in L); e=max(r['e'] for r in L)
    ds=sorted(r['d'] for r in L)
    rows.append((c,len(L),sum(ds),(e-s).total_seconds(),st.median(ds),ds[-1],(s-t0).total_seconds(),(e-t0).total_seconds()))
rows.sort(key=lambda r:-r[3])
print('%-48s %5s %8s %8s %7s %7s %7s %7s'%('class','n','sumdur','span','med','max','start','end'))
for r in rows[:top]: print('%-48s %5d %8.0f %8.0f %7.2f %7.1f %7.0f %7.0f'%r)
# concurrency timeline per minute
print('minute: running tests')
m=int((t1-t0).total_seconds()//60)+1
for i in range(0,m,2):
    a=t0+dt.timedelta(minutes=i)
    print(i, sum(1 for r in res if r['s']<=a<r['e']), end=' | ')
print()
# duration histogram
b=collections.Counter(min(int(r['d']//5)*5,60) for r in res)
print('hist(5s buckets):',sorted(b.items()))
