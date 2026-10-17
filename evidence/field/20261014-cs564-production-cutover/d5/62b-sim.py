import re, heapq, itertools
E={}
for l in open('62b-edges.txt',encoding='utf-8'):
    d=dict(re.findall(r'(\w+)=([^|]*?)\s*(?:\||$)',l.strip()))
    if not d: continue
    E.setdefault(int(d['StartNode']),[]).append((int(d['EndNode']),round(float(d['CostMm']))))
S={}
for l in open('62b-stations.txt',encoding='utf-8'):
    d=dict(re.findall(r'(\w+)=([^|]*?)\s*(?:\||$)',l.strip()))
    if d: S[int(d['StationId'])]=int(d['ResolvedNode'])
def dist(a,b):
    o=S[a];t=S[b];best={o:0};q=[(0,o)]
    while q:
        c,n=heapq.heappop(q)
        if c>best.get(n,1e18): continue
        if n==t: return c
        for m,w in E.get(n,[]):
            if c+w<best.get(m,1e18): best[m]=c+w; heapq.heappush(q,(c+w,m))
    return None
def path(start,stops): 
    tot=0;f=start
    for s in stops: tot+=dist(f,s); f=s
    return tot
A=65000; C=126
def evaluate(label,cur,ahead):
    # ahead[0] is current-next stop (the one the car stands at / drives to); unload 220 is last; candidate unload merges into 220
    orig=path(cur,ahead); res=[]
    for i in range(1,len(ahead)):  # insert pickup after current-next, before unload (unload merged into last 220)
        ins=ahead[:i]+[C]+ahead[i:]
        res.append((path(cur,ins)-orig,i))
    best=min(res)
    print(f"{label}: car@{cur} plan={ahead} legs_total? ; best insert pos {best[1]} marginal={best[0]} mm -> {'OK' if best[0]<=A else 'DELAY_GATE_EXCEEDED'}; all={sorted(res)}")
print("dist 145->126",dist(145,126),"126->220",dist(126,220),"140->126",dist(140,126),"126->140",dist(126,140))
# W1 15:28:26-15:32:56 at 145; stops ahead in final-sequence order among those existing
evaluate("W1 15:28:26 at T2-1(145)",145,[145,164,154,150,140,220])
evaluate("W2 15:33:52 at T6-8(164)",164,[164,160,154,150,140,220])
evaluate("W3 15:40:54 at T6-7(160)",160,[160,160,154,150,140,220])
evaluate("W4 15:46:12 at T6-7(160)",160,[160,160,154,150,140,220])
evaluate("W5 15:52 at T6-1(154)",154,[154,150,140,220])
evaluate("W6 15:59 at T5-5(150)",150,[150,140,220])
evaluate("W7 16:08 at T2-5(140)",140,[140,220])
